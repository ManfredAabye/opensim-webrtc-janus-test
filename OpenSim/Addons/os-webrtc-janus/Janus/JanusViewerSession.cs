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

using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System;

using OMV = OpenMetaverse;
using OpenMetaverse.StructuredData;

using log4net;

namespace WebRtcVoice
{
    public class JanusViewerSession : IVoiceViewerSession
    {
        protected static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);
        protected static readonly string LogHeader = "[JANUS VIEWER SESSION]";

        // 'viewer_session' that is passed to and from the viewer
        // IVoiceViewerSession.ViewerSessionID
        public string ViewerSessionID { get; set; }
        // IVoiceViewerSession.VoiceService
        public IWebRtcVoiceService VoiceService { get; set; }
        // The Janus server keeps track of the user by this ID
        // IVoiceViewerSession.VoiceServiceSessionId
        public string VoiceServiceSessionId { get; set; }
        // IVoiceViewerSession.RegionId
        public OMV.UUID RegionId { get; set; }
        // IVoiceViewerSession.AgentId
        public OMV.UUID AgentId { get; set; }

        // Janus keeps track of the user by this ID
        public long ParticipantId { get; set; }

        // Connections to the Janus server
        public JanusSession Session { get; set; }
        public JanusAudioBridge AudioBridge { get; set; }
        public JanusRoom Room { get; set; }

        // This keeps copies of the offer/answer incase we need to resend
        public string OfferOrig { get; set; }
        public string Offer { get; set; }
        // Contains "type" and "sdp" fields
        public OSDMap Answer { get; set; }

        public int SpatialPosition { get; private set; } = 50;
        public int SpatialPositionFrontBack { get; private set; } = 100;
        public string SpatialAudioPositionPreset { get; private set; } = "front";

        private int _disconnectStarted;
        public string DisconnectReason { get; private set; }
        private readonly SemaphoreSlim _provisionLock = new SemaphoreSlim(1, 1);
        public SemaphoreSlim ProvisionLock => _provisionLock;

        public JanusViewerSession(IWebRtcVoiceService pVoiceService)
        {
            ViewerSessionID = OMV.UUID.Random().ToString();
            VoiceService = pVoiceService;
            m_log.DebugFormat("{0} JanusViewerSession created {1}", LogHeader, ViewerSessionID);
        }
        public JanusViewerSession(string pViewerSessionID, IWebRtcVoiceService pVoiceService)
        {
            ViewerSessionID = pViewerSessionID;
            VoiceService = pVoiceService;
            m_log.DebugFormat("{0} JanusViewerSession created {1}", LogHeader, ViewerSessionID);
        }

        public bool TryStartDisconnect(string pReason)
        {
            if (Interlocked.CompareExchange(ref _disconnectStarted, 1, 0) == 0)
            {
                DisconnectReason = pReason;
                return true;
            }
            return false;
        }

        public bool UpdateSpatialAudioFromRequest(OSDMap pRequest)
        {
            if (pRequest is null)
                return false;

            int newLeftRight = SpatialPosition;
            int newFrontBack = SpatialPositionFrontBack;
            string newPreset = SpatialAudioPositionPreset;
            bool changed = false;

            string preset = null;
            if (pRequest.TryGetString("spatial_audio_position", out string spatialAudioPosition))
                preset = spatialAudioPosition;
            else if (pRequest.TryGetString("spatial_position_name", out string spatialPositionName))
                preset = spatialPositionName;

            if (!String.IsNullOrWhiteSpace(preset))
            {
                switch (preset.Trim().ToLowerInvariant())
                {
                    case "left":
                        newLeftRight = 0;
                        newFrontBack = 50;
                        newPreset = "left";
                        break;
                    case "right":
                        newLeftRight = 100;
                        newFrontBack = 50;
                        newPreset = "right";
                        break;
                    case "front":
                        newLeftRight = 50;
                        newFrontBack = 100;
                        newPreset = "front";
                        break;
                    case "rear":
                        newLeftRight = 50;
                        newFrontBack = 0;
                        newPreset = "rear";
                        break;
                    case "center":
                        newLeftRight = 50;
                        newFrontBack = 50;
                        newPreset = "center";
                        break;
                }
            }

            if (pRequest.TryGetValue("spatial_position", out OSD spatialPosition))
            {
                newLeftRight = Math.Clamp(spatialPosition.AsInteger(), 0, 100);
                newPreset = "custom";
            }
            if (pRequest.TryGetValue("spatial_position_fb", out OSD spatialPositionFb))
            {
                newFrontBack = Math.Clamp(spatialPositionFb.AsInteger(), 0, 100);
                newPreset = "custom";
            }

            if (newLeftRight != SpatialPosition || newFrontBack != SpatialPositionFrontBack || newPreset != SpatialAudioPositionPreset)
            {
                SpatialPosition = newLeftRight;
                SpatialPositionFrontBack = newFrontBack;
                SpatialAudioPositionPreset = newPreset;
                changed = true;
            }

            return changed;
        }

        // Send the messages to the voice service to try and get rid of the session
        // IVoiceViewerSession.Shutdown
        public async Task Shutdown()
        {
            m_log.DebugFormat("{0} JanusViewerSession shutdown {1}", LogHeader, ViewerSessionID);
            if (Room is not null)
            {
                var rm = Room;
                Room = null;
                await rm.LeaveRoom(this);
            }
            if (AudioBridge is not null)
            {
                var ab = AudioBridge;
                AudioBridge = null;
                await ab.Detach();
            }   
            if (Session is not null)
            {
                var s = Session;
                Session = null;
                await s.DestroySession();
                s.Dispose();
            }
        }
    }
}
