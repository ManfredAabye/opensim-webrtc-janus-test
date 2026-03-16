/*
 * Copyright (c) Contributors, http://opensimulator.org/
 * See CONTRIBUTORS.TXT for a full list of copyright holders.
 *
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the following conditions are met:
 *     * Redistributions of source code must retain the above copyright
 *       notice, this list of conditions and the following disclaimer.
 *     * Redistributions in binary form must reproduce the above copyright
 *       notice, this list of conditions and the following disclaimer in the
 *       documentation and/or other materials provided with the distribution.
 *     * Neither the name of the OpenSimulator Project nor the
 *       names of its contributors may be used to endorse or promote products
 *       derived from this software without specific prior written permission.
 *
 * THIS SOFTWARE IS PROVIDED BY THE DEVELOPERS ``AS IS'' AND ANY
 * EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
 * WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
 * DISCLAIMED. IN NO EVENT SHALL THE CONTRIBUTORS BE LIABLE FOR ANY
 * DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
 * (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
 * LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
 * ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
 * (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
 * SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
 */

using System;
using System.Reflection;

using OpenSim.Framework;
using OpenSim.Services.Interfaces;
using OpenSim.Services.Base;

using OpenMetaverse.StructuredData;
using OpenMetaverse;

using Nini.Config;
using log4net;
using System.Threading.Tasks;
using System.Text.RegularExpressions;
using System.Collections.Generic;

namespace WebRtcVoice
{
    // Encapsulization of a Session to the Janus server
    public class JanusRoom : IDisposable
    {
        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);
        private static readonly string LogHeader = "[JANUS ROOM]";

        public int RoomId { get; private set; }

        private JanusPlugin _AudioBridge;

        // Wrapper around the session connection to Janus-gateway
        public JanusRoom(JanusPlugin pAudioBridge, int pRoomId)
        {
            _AudioBridge = pAudioBridge;
            RoomId = pRoomId;
        }

        public void Dispose()
        {
            // Close the room
        }

        private static bool IsBenignJanusLeaveError(JanusMessageResp pResp, out int pJanusErrorCode, out string pJanusErrorReason)
        {
            pJanusErrorCode = 0;
            pJanusErrorReason = String.Empty;
            if (pResp is null || !pResp.isError)
                return false;

            ErrorResp errResp = new ErrorResp(pResp);
            pJanusErrorCode = errResp.errorCode;
            pJanusErrorReason = errResp.errorReason;

            return pJanusErrorCode == 458 || pJanusErrorCode == 459;
        }

        public async Task<bool> JoinRoom(JanusViewerSession pVSession)
        {
            bool ret = false;
            try
            {
                // m_log.DebugFormat("{0} JoinRoom. New joinReq for room {1}", LogHeader, RoomId);

                // Discovered that AudioBridge doesn't care if the data portion is present
                //    and, if removed, the viewer complains that the "m=" sections are
                //    out of order. Not "cleaning" (removing the data section) seems to work.
                // string cleanSdp = CleanupSdp(pSdp);
                var joinReq = new AudioBridgeJoinRoomReq(RoomId, pVSession.AgentId.ToString());
                joinReq.SetSpatialPosition(pVSession.SpatialPosition, pVSession.SpatialPositionFrontBack);
                // joinReq.SetJsep("offer", cleanSdp);
                joinReq.SetJsep("offer", pVSession.Offer);

                JanusMessageResp resp = await _AudioBridge.SendPluginMsg(joinReq);
                AudioBridgeJoinRoomResp joinResp = new AudioBridgeJoinRoomResp(resp);

                if (joinResp is not null && joinResp.AudioBridgeReturnCode == "joined" && joinResp.ParticipantId > 0)
                {
                    pVSession.ParticipantId = joinResp.ParticipantId;
                    pVSession.Answer = joinResp.Jsep;
                    ret = true;
                    m_log.DebugFormat("{0} JoinRoom. Joined room {1}. Participant={2}", LogHeader, RoomId, pVSession.ParticipantId);
                }
                else if (joinResp is not null && joinResp.AudioBridgeErrorCode == 491)
                {
                    m_log.WarnFormat("{0} JoinRoom. Already in a room for agent {1}. Attempting recovery.",
                            LogHeader, pVSession.AgentId);

                    bool recovered = await RecoverAlreadyInRoomAndLeave(pVSession.AgentId.ToString());
                    if (recovered)
                    {
                        var retryJoinReq = new AudioBridgeJoinRoomReq(RoomId, pVSession.AgentId.ToString());
                        retryJoinReq.SetJsep("offer", pVSession.Offer);
                        JanusMessageResp retryResp = await _AudioBridge.SendPluginMsg(retryJoinReq);
                        AudioBridgeJoinRoomResp retryJoinResp = new AudioBridgeJoinRoomResp(retryResp);

                        if (retryJoinResp is not null && retryJoinResp.AudioBridgeReturnCode == "joined" && retryJoinResp.ParticipantId > 0)
                        {
                            pVSession.ParticipantId = retryJoinResp.ParticipantId;
                            pVSession.Answer = retryJoinResp.Jsep;
                            ret = true;
                            m_log.InfoFormat("{0} JoinRoom. Recovery succeeded for room {1}. Participant={2}",
                                    LogHeader, RoomId, pVSession.ParticipantId);
                        }
                        else
                        {
                            m_log.ErrorFormat("{0} JoinRoom. Recovery retry failed for room {1}",
                                    LogHeader, RoomId);
                            if (m_log.IsDebugEnabled)
                                m_log.DebugFormat("{0} JoinRoom. Recovery retry detail: {1}", LogHeader, retryJoinResp?.ToString() ?? "null");
                        }
                    }
                    else
                    {
                        m_log.ErrorFormat("{0} JoinRoom. Recovery failed: could not clear previous room membership",
                                LogHeader);
                        if (m_log.IsDebugEnabled)
                            m_log.DebugFormat("{0} JoinRoom. Recovery failed detail: {1}", LogHeader, joinResp.ToString());
                    }
                }
                else
                {
                    if (joinResp is not null && joinResp.AudioBridgeReturnCode == "joined" && joinResp.ParticipantId <= 0)
                    {
                        m_log.ErrorFormat("{0} JoinRoom. Joined response contains invalid participant id {1} for room {2}",
                                LogHeader, joinResp.ParticipantId, RoomId);
                        if (m_log.IsDebugEnabled)
                            m_log.DebugFormat("{0} JoinRoom. Invalid participant detail: {1}", LogHeader, joinResp.ToString());
                    }
                    m_log.ErrorFormat("{0} JoinRoom. Failed to join room {1}", LogHeader, RoomId);
                    if (m_log.IsDebugEnabled)
                        m_log.DebugFormat("{0} JoinRoom. Failure detail: {1}", LogHeader, joinResp?.ToString() ?? "null");
                }
            }
            catch (Exception e)
            {
                m_log.ErrorFormat("{0} JoinRoom. Exception {1}", LogHeader, e);
            }
            return ret;
        }

        public async Task<bool> ConfigureSpatialAudio(JanusViewerSession pVSession)
        {
            if (pVSession is null || pVSession.ParticipantId <= 0)
                return false;

            try
            {
                AudioBridgeConfigRoomReq req = new AudioBridgeConfigRoomReq();
                req.SetSpatialPosition(pVSession.SpatialPosition, pVSession.SpatialPositionFrontBack);
                JanusMessageResp resp = await _AudioBridge.SendPluginMsg(req);
                if (resp is null)
                    return false;

                AudioBridgeResp abResp = new AudioBridgeResp(resp);
                string returnCode = abResp.AudioBridgeReturnCode;
                string janusReturnCode = resp.ReturnCode;
                int errorCode = abResp.AudioBridgeErrorCode;

                if (errorCode == 0 &&
                    (abResp.isSuccess || returnCode == "event" || returnCode == "success" || janusReturnCode == "ack"))
                {
                    m_log.InfoFormat("{0} ConfigureSpatialAudio. Updated room {1}, participant={2}, preset={3}, lr={4}, fb={5}",
                            LogHeader, RoomId, pVSession.ParticipantId, pVSession.SpatialAudioPositionPreset,
                            pVSession.SpatialPosition, pVSession.SpatialPositionFrontBack);
                    return true;
                }

                bool benignJanusError = IsBenignJanusLeaveError(resp, out int janusErrorCode, out string janusErrorReason);
                if (benignJanusError)
                {
                    m_log.InfoFormat("{0} ConfigureSpatialAudio. Treating stale Janus state as already gone for room {1}, participant={2} (janusErrorCode={3}, reason={4})",
                            LogHeader, RoomId, pVSession.ParticipantId, janusErrorCode, janusErrorReason);
                    return true;
                }

                m_log.ErrorFormat("{0} ConfigureSpatialAudio. Failed room {1}, participant={2}, janus={3}, audiobridge={4}, errorCode={5}",
                        LogHeader, RoomId, pVSession.ParticipantId, janusReturnCode, returnCode, errorCode);
            }
            catch (Exception e)
            {
                m_log.ErrorFormat("{0} ConfigureSpatialAudio. Exception {1}", LogHeader, e);
            }

            return false;
        }

        public async Task<bool> ConfigureParticipantAudio(JanusViewerSession pVSession)
        {
            if (pVSession is null || pVSession.ParticipantId <= 0)
                return false;

            try
            {
                AudioBridgeConfigRoomReq req = new AudioBridgeConfigRoomReq();
                req.SetMuted(pVSession.ParticipantMuted);
                req.SetVolumeGain(pVSession.ParticipantVolumeGain);
                JanusMessageResp resp = await _AudioBridge.SendPluginMsg(req);
                if (resp is null)
                    return false;

                AudioBridgeResp abResp = new AudioBridgeResp(resp);
                string returnCode = abResp.AudioBridgeReturnCode;
                string janusReturnCode = resp.ReturnCode;
                int errorCode = abResp.AudioBridgeErrorCode;

                if (errorCode == 0 &&
                    (abResp.isSuccess || returnCode == "event" || returnCode == "success" || janusReturnCode == "ack"))
                {
                    m_log.InfoFormat("{0} ConfigureParticipantAudio. Updated room {1}, participant={2}, muted={3}, gain={4}",
                            LogHeader, RoomId, pVSession.ParticipantId, pVSession.ParticipantMuted, pVSession.ParticipantVolumeGain);
                    return true;
                }

                bool benignJanusError = IsBenignJanusLeaveError(resp, out int janusErrorCode, out string janusErrorReason);
                if (benignJanusError)
                {
                    m_log.InfoFormat("{0} ConfigureParticipantAudio. Treating stale Janus state as already gone for room {1}, participant={2} (janusErrorCode={3}, reason={4})",
                            LogHeader, RoomId, pVSession.ParticipantId, janusErrorCode, janusErrorReason);
                    return true;
                }

                m_log.ErrorFormat("{0} ConfigureParticipantAudio. Failed room {1}, participant={2}, janus={3}, audiobridge={4}, errorCode={5}",
                        LogHeader, RoomId, pVSession.ParticipantId, janusReturnCode, returnCode, errorCode);
            }
            catch (Exception e)
            {
                m_log.ErrorFormat("{0} ConfigureParticipantAudio. Exception {1}", LogHeader, e);
            }

            return false;
        }

        private async Task<bool> RecoverAlreadyInRoomAndLeave(string pDisplay)
        {
            try
            {
                JanusMessageResp listRoomsRespRaw = await _AudioBridge.SendPluginMsg(new AudioBridgeListRoomsReq());
                AudioBridgeResp listRoomsResp = new AudioBridgeResp(listRoomsRespRaw);
                if (listRoomsResp?.PluginRespData is null ||
                    !listRoomsResp.PluginRespData.TryGetValue("list", out OSD roomListNode) ||
                    roomListNode is not OSDArray roomList)
                {
                    return false;
                }

                foreach (OSD roomNode in roomList)
                {
                    if (roomNode is not OSDMap roomMap ||
                        !roomMap.TryGetValue("room", out OSD roomIdNode))
                        continue;

                    int roomId = roomIdNode.AsInteger();
                    if (roomId <= 0)
                        continue;

                    JanusMessageResp listParticipantsRespRaw = await _AudioBridge.SendPluginMsg(new AudioBridgeListParticipantsReq(roomId));
                    AudioBridgeResp listParticipantsResp = new AudioBridgeResp(listParticipantsRespRaw);
                    if (listParticipantsResp?.PluginRespData is null ||
                        !listParticipantsResp.PluginRespData.TryGetValue("participants", out OSD participantsNode) ||
                        participantsNode is not OSDArray participants)
                        continue;

                    foreach (OSD participantNode in participants)
                    {
                        if (participantNode is not OSDMap participant)
                            continue;

                        string display = participant.TryGetValue("display", out OSD displayNode) ? displayNode.AsString() : String.Empty;
                        if (!String.Equals(display, pDisplay, StringComparison.Ordinal))
                            continue;

                        long participantId = participant.TryGetValue("id", out OSD idNode) ? JanusMessage.OSDToLong(idNode) : 0L;
                        if (participantId <= 0)
                            continue;

                        JanusMessageResp leaveRespRaw = await _AudioBridge.SendPluginMsg(new AudioBridgeLeaveRoomReq(roomId, participantId));
                        AudioBridgeResp leaveResp = new AudioBridgeResp(leaveRespRaw);

                        if (leaveResp is not null)
                        {
                            int errorCode = leaveResp.AudioBridgeErrorCode;
                            string abCode = leaveResp.AudioBridgeReturnCode;
                            string janusCode = leaveRespRaw.ReturnCode;
                            bool benignJanusLeaveError = IsBenignJanusLeaveError(leaveRespRaw, out int janusErrorCode, out string janusErrorReason);

                            if (errorCode == 0 || abCode == "left" || abCode == "event" || janusCode == "ack" || benignJanusLeaveError)
                            {
                                if (benignJanusLeaveError)
                                {
                                    m_log.InfoFormat("{0} RecoverAlreadyInRoomAndLeave. Treating Janus stale state as cleared for participant {1} in room {2} (janusErrorCode={3}, reason={4}).",
                                            LogHeader, participantId, roomId, janusErrorCode, janusErrorReason);
                                }
                                else
                                {
                                    m_log.InfoFormat("{0} RecoverAlreadyInRoomAndLeave. Cleared stale participant {1} from room {2}.",
                                            LogHeader, participantId, roomId);
                                }
                                return true;
                            }
                        }
                    }
                }
            }
            catch (Exception e)
            {
                m_log.ErrorFormat("{0} RecoverAlreadyInRoomAndLeave. Exception {1}", LogHeader, e);
            }

            return false;
        }

        // TODO: this doesn't work.
        // Not sure if it is needed. Janus generates Hangup events when the viewer leaves.
        /*
        public async Task<bool> Hangup(JanusViewerSession pAttendeeSession)
        {
            bool ret = false;
            try
            {
            }
            catch (Exception e)
            {
                m_log.ErrorFormat("{0} LeaveRoom. Exception {1}", LogHeader, e);
            }
            return ret;
        }
        */

        public async Task<bool> LeaveRoom(JanusViewerSession pAttendeeSession)
        {
            bool ret = false;
            try
            {
                if (pAttendeeSession is null || pAttendeeSession.ParticipantId <= 0)
                {
                    m_log.DebugFormat("{0} LeaveRoom. Skip leave for room {1}, invalid participant={2}",
                            LogHeader, RoomId, pAttendeeSession?.ParticipantId ?? 0L);
                    return true;
                }

                JanusMessageResp resp = await _AudioBridge.SendPluginMsg(
                    new AudioBridgeLeaveRoomReq(RoomId, pAttendeeSession.ParticipantId));

                if (resp is null)
                {
                    m_log.ErrorFormat("{0} LeaveRoom. Null response for room {1}, participant={2}",
                            LogHeader, RoomId, pAttendeeSession.ParticipantId);
                    return false;
                }

                AudioBridgeResp abResp = new AudioBridgeResp(resp);
                string returnCode = abResp.AudioBridgeReturnCode;
                string janusReturnCode = resp.ReturnCode;
                int errorCode = abResp.AudioBridgeErrorCode;
                bool benignJanusLeaveError = IsBenignJanusLeaveError(resp, out int janusErrorCode, out string janusErrorReason);
                bool isBenignAlreadyLeft =
                    errorCode == 487 &&
                    (returnCode == "event" || janusReturnCode == "event" || janusReturnCode == "ack");

                if (errorCode == 0 &&
                    (abResp.isSuccess || returnCode == "left" || returnCode == "event" || returnCode == "success" || janusReturnCode == "ack"))
                {
                    ret = true;
                    if (janusReturnCode == "ack" && String.IsNullOrEmpty(returnCode))
                    {
                        m_log.DebugFormat("{0} LeaveRoom. Ack accepted for room {1}, participant={2}",
                                LogHeader, RoomId, pAttendeeSession.ParticipantId);
                    }
                }
                    else if (isBenignAlreadyLeft)
                    {
                        ret = true;
                        m_log.InfoFormat("{0} LeaveRoom. Participant already left room {1}, participant={2} (errorCode=487)",
                            LogHeader, RoomId, pAttendeeSession.ParticipantId);
                    }
                else if (benignJanusLeaveError)
                {
                    ret = true;
                    m_log.InfoFormat("{0} LeaveRoom. Treating stale Janus state as already left for room {1}, participant={2} (janusErrorCode={3}, reason={4})",
                            LogHeader, RoomId, pAttendeeSession.ParticipantId, janusErrorCode, janusErrorReason);
                }
                else
                {
                    m_log.ErrorFormat("{0} LeaveRoom. Failed room {1}, participant={2}, janus={3}, audiobridge={4}, errorCode={5}, janusErrorCode={6}, reason={7}",
                            LogHeader, RoomId, pAttendeeSession.ParticipantId, janusReturnCode, returnCode, errorCode, janusErrorCode, janusErrorReason);
                }
            }
            catch (Exception e)
            {
                m_log.ErrorFormat("{0} LeaveRoom. Exception {1}", LogHeader, e);
            }
            return ret;
        }

    }
}
