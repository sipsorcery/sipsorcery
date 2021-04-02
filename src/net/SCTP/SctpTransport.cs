﻿//-----------------------------------------------------------------------------
// Filename: SctpTransport.cs
//
// Description: Represents a common SCTP transport layer.
//
// Remarks:
// The interface defined in https://tools.ietf.org/html/rfc4960#section-10 
// was used as a basis for this class.
//
// Author(s):
// Aaron Clauson (aaron@sipsorcery.com)
// 
// History:
// St Patrick's Day 2021	Aaron Clauson	Created, Dublin, Ireland.
//
// License: 
// BSD 3-Clause "New" or "Revised" License, see included LICENSE.md file.
//-----------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using SIPSorcery.Sys;
using TinyJson;

namespace SIPSorcery.Net
{
    /// <summary>
    /// The opaque cookie structure that will be sent in response to an SCTP INIT
    /// packet.
    /// </summary>
    public struct SctpTransportCookie
    {
        public static SctpTransportCookie Empty = new SctpTransportCookie() { _isEmpty = true };

        public ushort SourcePort { get; set; }
        public ushort DestinationPort { get; set; }
        public uint RemoteTag { get; set; }
        public uint RemoteTSN { get; set; }
        public uint RemoteARwnd { get; set; }
        public string RemoteEndPoint { get; set; }
        public uint Tag { get; set; }
        public uint TSN { get; set; }
        public uint ARwnd { get; set; }
        public string CreatedAt { get; set; }
        public int Lifetime { get; set; }
        public string HMAC { get; set; }

        private bool _isEmpty;

        public bool IsEmpty()
        {
            return _isEmpty;
        }
    }

    /// <summary>
    /// Contains the common methods that an SCTP transport layer needs to implement.
    /// As well as being able to be carried directly in IP packets, SCTP packets can
    /// also be wrapped in higher level protocols.
    /// </summary>
    public abstract class SctpTransport
    {
        private const int HMAC_KEY_SIZE = 64;

        /// <summary>
        /// As per https://tools.ietf.org/html/rfc4960#section-15.
        /// </summary>
        public const int DEFAULT_COOKIE_LIFETIME_SECONDS = 60;

        private static ILogger logger = SIPSorcery.LogFactory.CreateLogger<SctpTransport>();

        /// <summary>
        /// Ephemeral secret key to use for generating cookie HMAC's. The purpose of the HMAC is
        /// to prevent resource depletion attacks. This does not justify using an external key store.
        /// </summary>
        private static byte[] _hmacKey = new byte[HMAC_KEY_SIZE];

        public abstract void Send(string associationID, byte[] buffer, int offset, int length);

        static SctpTransport()
        {
            Crypto.GetRandomBytes(_hmacKey);
        }

        /// <summary>
        /// Gets a cookie to send in an INIT ACK chunk. This method
        /// is overloadable so that different transports can tailor how the cookie
        /// is created. For example the WebRTC SCTP transport only ever uses a
        /// single association so the local Tag and TSN properties must be
        /// the same rather than random.
        /// </summary>
        protected virtual SctpTransportCookie GetInitAckCookie(
            ushort sourcePort,
            ushort destinationPort,
            uint remoteTag,
            uint remoteTSN,
            uint remoteARwnd,
            string remoteEndPoint,
            int lifeTimeExtension = 0)
        {
            var cookie = new SctpTransportCookie
            {
                SourcePort = sourcePort,
                DestinationPort = destinationPort,
                RemoteTag = remoteTag,
                RemoteTSN = remoteTSN,
                RemoteARwnd = remoteARwnd,
                RemoteEndPoint = remoteEndPoint,
                Tag = Crypto.GetRandomUInt(),
                TSN = Crypto.GetRandomUInt(),
                ARwnd = SctpAssociation.DEFAULT_ADVERTISED_RECEIVE_WINDOW,
                CreatedAt = DateTime.Now.ToString("o"),
                Lifetime = DEFAULT_COOKIE_LIFETIME_SECONDS + lifeTimeExtension,
                HMAC = string.Empty
            };

            return cookie;
        }

        /// <summary>
        /// Creates the INIT ACK chunk and packet to send as a response to an SCTP
        /// packet containing an INIT chunk.
        /// </summary>
        /// <param name="initPacket">The received packet containing the INIT chunk.</param>
        /// <param name="remoteEP">Optional. The remote IP end point the INIT packet was
        /// received on. For transports that don't use an IP transport directly this parameter
        /// can be set to null and it will not form part of the COOKIE ECHO checks.</param>
        /// <returns>An SCTP packet with a single INIT ACK chunk.</returns>
        protected SctpPacket GetInitAck(SctpPacket initPacket, IPEndPoint remoteEP)
        {
            SctpInitChunk initChunk = initPacket.GetChunks().Single(x => x.KnownType == SctpChunkType.INIT) as SctpInitChunk;

            SctpPacket initAckPacket = new SctpPacket(
                initPacket.Header.DestinationPort,
                initPacket.Header.SourcePort,
                initChunk.InitiateTag);

            var cookie = GetInitAckCookie(
                initPacket.Header.DestinationPort,
                initPacket.Header.SourcePort,
                initChunk.InitiateTag,
                initChunk.InitialTSN,
                initChunk.ARwnd,
                remoteEP != null ? remoteEP.ToString() : string.Empty,
                (int)(initChunk.CookiePreservative / 1000));

            var json = cookie.ToJson();
            var jsonBuffer = Encoding.UTF8.GetBytes(json);

            using (HMACSHA256 hmac = new HMACSHA256(_hmacKey))
            {
                var result = hmac.ComputeHash(jsonBuffer);
                cookie.HMAC = result.HexStr();
            }

            var jsonWithHMAC = cookie.ToJson();
            var jsonBufferWithHMAC = Encoding.UTF8.GetBytes(jsonWithHMAC);

            SctpInitChunk initAckChunk = new SctpInitChunk(SctpChunkType.INIT_ACK, cookie.Tag, cookie.TSN, cookie.ARwnd);
            initAckChunk.StateCookie = jsonBufferWithHMAC;
            initAckChunk.UnrecognizedPeerParameters = initChunk.UnrecognizedPeerParameters;

            initAckPacket.AddChunk(initAckChunk);

            return initAckPacket;
        }

        /// <summary>
        /// A COOKIE ECHO chunk is the step in the handshake that a new SCTP association will be created
        /// for a remote party. Providing the state cookie is valid create a new association and return it to the
        /// parent transport.
        /// </summary>
        /// <param name="cookieEcho">A COOKIE ECHO chunk received from the remote party.</param>
        /// <param name="error">If there is a problem with the COOKIE ECHO chunk then the error output
        /// parameter will be set with a packet to send back to the remote party.</param>
        /// <returns>If the state cookie in the chunk is valid a new SCTP association will be returned. IF
        /// it's not valid null will be returned.</returns>
        protected SctpTransportCookie GetCookie(SctpChunk cookieEcho, out SctpPacket error)
        {
            error = null;

            var cookieBuffer = cookieEcho.ChunkValue;
            var cookie = JSONParser.FromJson<SctpTransportCookie>(Encoding.UTF8.GetString(cookieBuffer));

            logger.LogDebug($"Cookie: {cookie.ToJson()}");

            string calculatedHMAC = GetCookieHMAC(cookieBuffer);
            if (calculatedHMAC != cookie.HMAC)
            {
                logger.LogWarning($"SCTP COOKIE ECHO chunk had an invalid HMAC, calculated {calculatedHMAC}, cookie {cookie.HMAC}.");
                // TODO.
                //error = new SctpPacket();
                return SctpTransportCookie.Empty;
            }
            else if(DateTime.Now.Subtract(DateTime.Parse(cookie.CreatedAt)).TotalSeconds > cookie.Lifetime)
            {
                logger.LogWarning($"SCTP COOKIE ECHO chunk was stale, created at {cookie.CreatedAt}, now {DateTime.Now.ToString("o")}, lifetime {cookie.Lifetime}s.");
                // TODO.
                //error = new SctpPacket();
                return SctpTransportCookie.Empty;
            }
            else
            {
                return cookie;
            }
        }

        /// <summary>
        /// Checks whether the state cookie that is supplied in a COOKIE ECHO chunk is valid for
        /// this SCTP transport.
        /// </summary>
        /// <param name="buffer">The buffer holding the state cookie.</param>
        /// <returns>True if the cookie is determined as valid, false if not.</returns>
        protected string GetCookieHMAC(byte[] buffer)
        {
            var cookie = JSONParser.FromJson<SctpTransportCookie>(Encoding.UTF8.GetString(buffer));
            string hmacCalculated = null;
            cookie.HMAC = string.Empty;

            byte[] cookiePreImage = Encoding.UTF8.GetBytes(cookie.ToJson());

            using (HMACSHA256 hmac = new HMACSHA256(_hmacKey))
            {
                var result = hmac.ComputeHash(cookiePreImage);
                hmacCalculated = result.HexStr();
            }

            return hmacCalculated;
        }

        /// <summary>
        /// This method allows SCTP to initialise its internal data structures
        /// and allocate necessary resources for setting up its operation
        /// environment.
        /// </summary>
        /// <param name="localPort">SCTP port number, if the application wants it to be specified.</param>
        /// <returns>The local SCTP instance name.</returns>
        public string Initialize(ushort localPort)
        {
            return "local SCTP instance name";
        }

        /// <summary>
        /// Initiates an association to a specific peer end point
        /// </summary>
        /// <param name="destination"></param>
        /// <param name="streamCount"></param>
        /// <returns>An association ID, which is a local handle to the SCTP association.</returns>
        public string Associate(IPAddress destination, int streamCount)
        {
            return "association ID";
        }

        /// <summary>
        /// Gracefully closes an association. Any locally queued user data will
        /// be delivered to the peer.The association will be terminated only
        /// after the peer acknowledges all the SCTP packets sent.
        /// </summary>
        /// <param name="associationID">Local handle to the SCTP association.</param>
        public void Shutdown(string associationID)
        {

        }

        /// <summary>
        /// Ungracefully closes an association. Any locally queued user data
        /// will be discarded, and an ABORT chunk is sent to the peer.
        /// </summary>
        /// <param name="associationID">Local handle to the SCTP association.</param>
        public void Abort(string associationID)
        {

        }

        /// <summary>
        /// This is the main method to send user data via SCTP.
        /// </summary>
        /// <param name="associationID">Local handle to the SCTP association.</param>
        /// <param name="buffer">The buffer holding the data to send.</param>
        /// <param name="length">The number of bytes from the buffer to send.</param>
        /// <param name="contextID">Optional. A 32-bit integer that will be carried in the
        /// sending failure notification to the application if the transportation of
        /// this user message fails.</param>
        /// <param name="streamID">Optional. To indicate which stream to send the data on. If not
        /// specified, stream 0 will be used.</param>
        /// <param name="lifeTime">Optional. specifies the life time of the user data. The user
        /// data will not be sent by SCTP after the life time expires.This
        /// parameter can be used to avoid efforts to transmit stale user
        /// messages.</param>
        /// <returns></returns>
        public string Send(string associationID, byte[] buffer, int length, int contextID, int streamID, int lifeTime)
        {
            return "ok";
        }

        /// <summary>
        /// Instructs the local SCTP to use the specified destination transport
        /// address as the primary path for sending packets.
        /// </summary>
        /// <param name="associationID"></param>
        /// <returns></returns>
        public string SetPrimary(string associationID)
        {
            // Note: Seems like this will be a noop for SCTP encapsulated in UDP.
            return "ok";
        }

        /// <summary>
        /// This method shall read the first user message in the SCTP in-queue
        /// into the buffer specified by the application, if there is one available.The
        /// size of the message read, in bytes, will be returned.
        /// </summary>
        /// <param name="associationID">Local handle to the SCTP association.</param>
        /// <param name="buffer">The buffer to place the received data into.</param>
        /// <param name="length">The maximum size of the data to receive.</param>
        /// <param name="streamID">Optional. If specified indicates which stream to 
        /// receive the data on.</param>
        /// <returns></returns>
        public int Receive(string associationID, byte[] buffer, int length, int streamID)
        {
            return 0;
        }

        /// <summary>
        /// Returns the current status of the association.
        /// </summary>
        /// <param name="associationID">Local handle to the SCTP association.</param>
        /// <returns></returns>
        public SctpStatus Status(string associationID)
        {
            return new SctpStatus();
        }

        /// <summary>
        /// Instructs the local endpoint to enable or disable heartbeat on the
        /// specified destination transport address.
        /// </summary>
        /// <param name="associationID">Local handle to the SCTP association.</param>
        /// <param name="interval">Indicates the frequency of the heartbeat if
        /// this is to enable heartbeat on a destination transport address.
        /// This value is added to the RTO of the destination transport
        /// address.This value, if present, affects all destinations.</param>
        /// <returns></returns>
        public string ChangeHeartbeat(string associationID, int interval)
        {
            return "ok";
        }

        /// <summary>
        /// Instructs the local endpoint to perform a HeartBeat on the specified
        /// destination transport address of the given association.
        /// </summary>
        /// <param name="associationID">Local handle to the SCTP association.</param>
        /// <returns>Indicates whether the transmission of the HEARTBEAT
        /// chunk to the destination address is successful.</returns>
        public string RequestHeartbeat(string associationID)
        {
            return "ok";
        }

        /// <summary>
        /// Instructs the local SCTP to report the current Smoothed Round Trip Time (SRTT)
        /// measurement on the specified destination transport address of the given 
        /// association.
        /// </summary>
        /// <param name="associationID">Local handle to the SCTP association.</param>
        /// <returns>An integer containing the most recent SRTT in milliseconds.</returns>
        public int GetSrttReport(string associationID)
        {
            return 0;
        }

        /// <summary>
        /// This method allows the local SCTP to customise the protocol
        /// parameters.
        /// </summary>
        /// <param name="associationID">Local handle to the SCTP association.</param>
        /// <param name="protocolParameters">The specific names and values of the
        /// protocol parameters that the SCTP user wishes to customise.</param>
        public void SetProtocolParameters(string associationID, object protocolParameters)
        {

        }

        /// <summary>
        /// ??
        /// </summary>
        /// <param name="dataRetrievalID">The identification passed to the application in the
        /// failure notification.</param>
        /// <param name="buffer">The buffer to store the received message.</param>
        /// <param name="length">The maximum size of the data to receive.</param>
        /// <param name="streamID">This is a return value that is set to indicate which
        /// stream the data was sent to.</param>
        public void ReceiveUnsent(string dataRetrievalID, byte[] buffer, int length, int streamID)
        {

        }

        /// <summary>
        /// ??
        /// </summary>
        /// <param name="dataRetrievalID">The identification passed to the application in the
        /// failure notification.</param>
        /// <param name="buffer">The buffer to store the received message.</param>
        /// <param name="length">The maximum size of the data to receive.</param>
        /// <param name="streamID">This is a return value that is set to indicate which
        /// stream the data was sent to.</param>
        public void ReceiveUnacknowledged(string dataRetrievalID, byte[] buffer, int length, int streamID)
        {

        }

        /// <summary>
        /// Release the resources for the specified SCTP instance.
        /// </summary>
        /// <param name="instanceName"></param>
        public void Destroy(string instanceName)
        {

        }
    }
}
