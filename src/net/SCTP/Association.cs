﻿/*
 * Copyright 2017 pi.pe gmbh .
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 *
 *     http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 *
 */
// Modified by Andrés Leone Gámez

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Org.BouncyCastle.Crypto.Tls;
using Org.BouncyCastle.Security;
using SCTP4CS.Utils;
using SIPSorcery.Sys;

/**
 *
 * @author Westhawk Ltd<thp@westhawk.co.uk>
 */
namespace SIPSorcery.Net.Sctp
{
    public enum AcknowlegeState
    { 
        Idle,//      int = iota // ack timer is off
        Immediate,            // will send ack immediately
        Delay                // ack timer is on (ack is being delayed)
    }
    // ack mode (for testing)
    public enum AckMode
    {
        Normal,
        NoDelay,
        AlwaysDelay
    }
    public enum ReconfigResult : uint
    {
        SuccessNOP = 0,
        SuccessPerformed = 1,
        Denied = 2,
        ErrorWrongSSN = 3,
        ErrorRequestAlreadyInProgress = 4,
        ErrorBadSequenceNumber = 5,
        ResultInProgress = 6
    }

    public struct AssociationConsts 
    {
        public const uint receiveMTU = 8192; // MTU for inbound packet (from DTLS)
        public const uint initialMTU = 1228; // initial MTU for outgoing packets (to DTLS)
        public const uint initialRecvBufSize = 1024 * 1024;
        public const uint commonHeaderSize = 12;
        public const uint dataChunkHeaderSize = 16;
        public const uint defaultMaxMessageSize = 65536;
    }

    public abstract class Association : IRunnable, IRtxTimerObserver, IAckTimerObserver
    {
        private UInt64 bytesReceived;
        private UInt64 bytesSent;
        protected uint myVerificationTag;

        protected Mutex RWMutex = new Mutex();

        private uint peerVerificationTag;

        private bool _even;
        private AckMode ackMode;
        protected bool IsClient;
        public abstract void associate();
        public AcknowlegeState ackState;

        /**
		 * <code>
		 *                     -----          -------- (from any state)
		 *                   /       \      /  rcv ABORT      [ABORT]
		 *  rcv INIT        |         |    |   ----------  or ----------
		 *  --------------- |         v    v   delete TCB     snd ABORT
		 *  generate Cookie  \    +---------+                 delete TCB
		 *  snd INIT ACK       ---|  CLOSED |
		 *                        +---------+
		 *                         /      \      [ASSOCIATE]
		 *                        /        \     ---------------
		 *                       |          |    create TCB
		 *                       |          |    snd INIT
		 *                       |          |    strt init timer
		 *        rcv valid      |          |
		 *      COOKIE  ECHO     |          v
		 *  (1) ---------------- |      +------------+
		 *      create TCB       |      | COOKIE-WAIT| (2)
		 *      snd COOKIE ACK   |      +------------+
		 *                       |          |
		 *                       |          |    rcv INIT ACK
		 *                       |          |    -----------------
		 *                       |          |    snd COOKIE ECHO
		 *                       |          |    stop init timer
		 *                       |          |    strt cookie timer
		 *                       |          v
		 *                       |      +--------------+
		 *                       |      | COOKIE-ECHOED| (3)
		 *                       |      +--------------+
		 *                       |          |
		 *                       |          |    rcv COOKIE ACK
		 *                       |          |    -----------------
		 *                       |          |    stop cookie timer
		 *                       v          v
		 *                     +---------------+
		 *                     |  ESTABLISHED  |
		 *                     +---------------+
		 * </code>
		 */
        public enum State
        {
            COOKIEWAIT, COOKIEECHOED, ESTABLISHED,
            SHUTDOWNPENDING, SHUTDOWNSENT, SHUTDOWNRECEIVED,
            SHUTDOWNACKSENT, CLOSED
        };

        // retransmission timer IDs
        public enum TimerType : int
        {
            T1Init,
            T1Cookie,
            T3RTX,
            Reconfig
        }

        private byte[] _supportedExtensions = { (byte)ChunkType.RE_CONFIG };
        /*
		 For what it is worth, here's the logic as to why we don't have any supported extensions.
		 { 
		 ASCONF, // this is ICE's job so we never send ASCONF or 
		 ASCONF-ACK, // ASCONF-ACK
		 FORWARDTSN, // we may end up wanting this - it supports partial reliability - aka giving up..
		 PKTDROP, // this is an optional performance enhancement especially valuable for middleboxes (we aren't one)
		 RE-CONFIG, // not sure about this - but lets assume for now that the w3c interface doesn't support stream resets.
		 AUTH // Assume DTLS will cover this for us if we never send ASCONF packets.
		 */
        ushort myMaxNumInboundStreams = ushort.MaxValue;
        ushort myMaxNumOutboundStreams = ushort.MaxValue;
        private static ILogger logger = Log.Logger;
        protected const int InitialRecvBufSize = 1024 * 1024;
        public static int COOKIESIZE = 32;
        private const uint commonHeaderSize = 12;
        private static long VALIDCOOKIELIFE = 60000;
        protected double _rto = 3.0;
        internal SimpleSCTPTimer _timer;
        private bool useForwardTSN;
        protected uint mtu = AssociationConsts.initialMTU;
        protected uint maxPayloadSize = AssociationConsts.initialMTU - (AssociationConsts.commonHeaderSize + AssociationConsts.dataChunkHeaderSize);
        private bool delayedAckTriggered;
        private CookieEchoChunk storedCookieEcho;
        private Dictionary<uint, ReConfigChunk> reconfigs = new Dictionary<uint, ReConfigChunk>();
        private bool immediateAckTriggered;
        private InitChunk storedInit;
        protected bool willRetransmitReconfig;
        /*
		 RTO.Initial - 3 seconds
		 RTO.Min - 1 second
		 RTO.Max - 60 seconds
		 Max.Burst - 4
		 RTO.Alpha - 1/8
		 RTO.Beta - 1/4
		 Valid.Cookie.Life - 60 seconds
		 Association.Max.Retrans - 10 attempts
		 Path.Max.Retrans - 5 attempts (per destination address)
		 Max.Init.Retransmits - 8 attempts
		 HB.interval - 30 seconds
		 HB.Max.Burst - 1
		 */
        protected DatagramTransport _transp;
        private Thread _rcv;
        private Thread _send;
        private SecureRandom _random;
        private bool willSendForwardTSN;
        protected uint cumulativeTSNAckPoint;
        private bool willRetransmitFast;
        private Dictionary<uint, OutgoingSSNResetRequestParameter> reconfigRequests = new Dictionary<uint, OutgoingSSNResetRequestParameter>();
        protected uint advancedPeerTSNAckPoint;

        // Congestion control parameters
        protected uint maxReceiveBufferSize = AssociationConsts.initialRecvBufSize;
        protected uint maxMessageSize = AssociationConsts.defaultMaxMessageSize;
        private uint cwnd;
        /*
         o  Congestion control window (cwnd, in bytes), which is adjusted by
         the sender based on observed network conditions.

         Note: This variable is maintained on a per-destination-address
         basis.
         */
        protected uint _cwnd
        {
            get
            {
                return cwnd;
            }
            set
            {
                if (cwnd != value)
                {
                    cwnd = value;
                }
            }
        }


        /*   
		 o  Receiver advertised window size (rwnd, in bytes), which is set by
		 the receiver based on its available buffer space for incoming
		 packets.

		 Note: This variable is kept on the entire association.
		 */
        private uint rwnd;
        protected uint _rwnd
        {
            get
            {
                return rwnd;
            }
            set
            {
                if (rwnd != value)
                {
                    rwnd = value;
                }
            }
        }
        // assume a single destination via ICE
        /*
			 o  Slow-start threshold (ssthresh, in bytes), which is used by the
			 sender to distinguish slow-start and congestion avoidance phases.

			 Note: This variable is maintained on a per-destination-address
			 basis.
			 */
        protected uint _ssthresh;
        private uint partialBytesAcked;
        private bool inFastRecovery;
        private uint fastRecoverExitPoint;

        private uint peerLastTSN;
        public static uint MAXBUFF = 20 * 1024;
        private const int receiveMTU = 8192;
        private int _srcPort;
        private int _destPort;
        private ConcurrentDictionary<int, SCTPStream> _streams;
        private AssociationListener _al;
        private Dictionary<long, DataChunk> _outbound;
        protected State _state;
        protected bool IsDone;
        private static int TICK = 1000; // loop time in rcv
        static int __assocNo = 1;
        private ReconfigState reconfigState;
        private uint myNextRSN;
        private uint minTSN2MeasureRTT; // for RTT measurement
        protected PendingQueue pendingQueue = new PendingQueue();
        protected PayloadQueue inflightQueue = new PayloadQueue();
        protected ControlQueue controlQueue = new ControlQueue();
        protected PayloadQueue payloadQueue = new PayloadQueue();
        // RTX & Ack timer
        private rtoManager rtoMgr;
        private rtxTimer t1Init;
        private rtxTimer t1Cookie;
        private rtxTimer t3RTX;
        private rtxTimer tReconfig;
        private ackTimer _ackTimer;
        private CookieHolder myCookie;
        private string name;

        //ackTimer* ackTimer
        protected uint myNextTSN;
        private bool Started;
        public bool IsFinished
        {
            get
            {
                return _rcv == null && Started;
            }
        }

        /// <summary>
        /// The next ID to use when creating a new stream. 
        /// Note originally this value as generated randomly between 0 and 65535 but Chrome was rejecting
        /// ID's that were greater than maximum number of streams set on the SCTP association. Hence
        /// changed it to be sequential.
        /// </summary>
        private int _nextStreamID = 0;

        class CookieHolder
        {
            public byte[] cookieData;
            public long cookieTime;
        };
        private List<CookieHolder> _cookies = new List<CookieHolder>();

        AssociationStats stats = new AssociationStats();

        // default is server
        public Association(DatagramTransport transport, AssociationListener al, int srcPort, int dstPort) : this(transport, al, false, srcPort, dstPort) { }

        public Association(DatagramTransport transport, AssociationListener al, bool client, int srcPort, int dstPort)
        {
            //logger.LogDebug($"SCTP created an Association of type: {this.GetType().Name}.");
            _al = al;
            _random = new SecureRandom();
            myVerificationTag = (uint)_random.NextInt();
            _transp = transport;
            _streams = new ConcurrentDictionary<int, SCTPStream>();
            _outbound = new Dictionary<long, DataChunk>();
            var IInt = new FastBit.Int(_random.NextInt());
            var tsn = new FastBit.Uint(IInt.b0, IInt.b1, IInt.b2, IInt.b3).Auint;
            myNextTSN = tsn;
            myNextRSN = tsn;
            minTSN2MeasureRTT = tsn;
            _state = State.CLOSED;

            _cwnd = min32(4 * mtu, max32(2 * mtu, 4380));

            rtoMgr = rtoManager.newRTOManager();
            t1Init = rtxTimer.newRTXTimer((int)TimerType.T1Init, this, rtoManager.maxInitRetrans);
            t1Cookie = rtxTimer.newRTXTimer((int)TimerType.T1Cookie, this, rtoManager.maxInitRetrans);
            t3RTX = rtxTimer.newRTXTimer((int)TimerType.T3RTX, this, rtoManager.noMaxRetrans);        // retransmit forever
            tReconfig = rtxTimer.newRTXTimer((int)TimerType.Reconfig, this, rtoManager.noMaxRetrans); // retransmit forever
            _ackTimer = ackTimer.newAckTimer(this);

            name = "AssocRcv" + Interlocked.Increment(ref __assocNo); 
            /*
			the method used to determine which
			side uses odd or even is based on the underlying DTLS connection
			role: the side acting as the DTLS client MUST use Streams with even
			Stream Identifiers, the side acting as the DTLS server MUST use
			Streams with odd Stream Identifiers. */
            _even = client;
            IsClient = client;
            _nextStreamID = (_even) ? _nextStreamID : _nextStreamID + 1;

            _srcPort = srcPort;
            _destPort = dstPort;

            cumulativeTSNAckPoint = tsn - 1;
            advancedPeerTSNAckPoint = tsn - 1;

            Init(client);
        }

        private void Init(bool isClient)
        {
            try
            {
                RWMutex.WaitOne();

                if (_transp != null)
                {
                    readLoop();
                    writeLoop();
                }
                else
                {
                    logger.LogError("Created an Association with a null transport somehow...");
                }

                if (isClient)
                {
                    _state = State.COOKIEWAIT;
                    InitChunk c = new InitChunk();
                    c.setInitialTSN(this.myNextTSN);
                    c.setNumInStreams(myMaxNumInboundStreams);
                    c.setNumOutStreams(myMaxNumOutboundStreams);
                    c.setAdRecWinCredit(maxReceiveBufferSize);
                    c.setInitiate(this.getMyVerTag());
                    storedInit = c;
                    sendInit();
                    t1Init.start(rtoMgr.getRTO());
                }
            }
            finally
            {
                RWMutex.ReleaseMutex();
            }
        }


        protected byte[] getSupportedExtensions()
        { // this lets others switch features off.
            return _supportedExtensions;
        }
        public uint getNearTSN()
        {
            return myNextTSN;
        }
        byte[] getUnionSupportedExtensions(byte[] far)
        {
            ByteBuffer unionbb = new ByteBuffer(new byte[far.Length]);
            for (int f = 0; f < far.Length; f++)
            {
                //logger.LogDebug($"offered extension {(ChunkType)far[f]}.");
                for (int n = 0; n < _supportedExtensions.Length; n++)
                {
                    //logger.LogDebug($"supported extension {(ChunkType)_supportedExtensions[n]}.");
                    if (_supportedExtensions[n] == far[f])
                    {
                        //logger.LogDebug($"matching extension {(ChunkType)_supportedExtensions[n]}.");
                        unionbb.Put(far[f]);
                    }
                }
            }
            byte[] res = new byte[unionbb.Position];
            unionbb.Position = 0;
            unionbb.GetBytes(res, res.Length);
            //logger.LogDebug("union of extensions contains :" + Chunk.chunksToNames(res));
            return res;
        }

        uint getMyReceiverWindowCredit()
        {
            uint bytesQueued = 0;
            foreach (var s in _streams)
            {
                bytesQueued += s.Value.getNumBytesInQueue();
            }

            if (bytesQueued >= maxReceiveBufferSize)
            {
                return 0;
            }
            return maxReceiveBufferSize - bytesQueued;
        }

        public void handleInbound(Packet rec)
        {
            try
            {
                checkPacket(rec);
            }
            catch (Exception e)
            {
                Log.Logger.LogWarning(e.Message);
                return;
            }

            handleChunkStart();

            //List<Chunk> replies = new List<Chunk>();
            var cl = rec.getChunkList();
            foreach (var c in cl)
            {
                c.validate();
            }
            foreach (var c in cl)
            {
                handleChunk(rec, c);
            }

            handleChunkEnd();

            //// find the highest sack.
            //Chunk hisack = null;
            //foreach (var c in replies)
            //{
            //    if (c.getType() == ChunkType.SACK)
            //    {
            //        if (hisack == null || ((SackChunk)c).getCumuTSNAck() > ((SackChunk)hisack).getCumuTSNAck())
            //        {
            //            hisack = c;
            //        }
            //    }
            //}
            //// remove all sacks
            //replies.RemoveAll((Chunk c) =>
            //{
            //    return c.getType() == ChunkType.SACK;
            //});
            //// insert the highest one first.
            //if (hisack != null)
            //{
            //    replies.Insert(0, hisack);
            //}

            //foreach (var reply in replies)
            //{
            //    controlQueue.push(reply);
            //}
        }

        private object myLock = new object();
        void handleChunkStart()
        {
            lock (myLock)
            {
                delayedAckTriggered = false;
                immediateAckTriggered = false;
            }
        }

        void handleChunkEnd()
        {
            lock (myLock)
            {
                if (immediateAckTriggered)
                {
                    // Send SACK now!
                    ackState = AcknowlegeState.Immediate;
                    _ackTimer.stop();
                }
                else if (delayedAckTriggered)
                {
                    // Will send delayed ack in the next ack timeout
                    ackState = AcknowlegeState.Delay;
                    _ackTimer.start();
                }
            }
        }

        void checkPacket(Packet p)
        {
            // All packets must adhere to these rules

            // This is the SCTP sender's port number.  It can be used by the
            // receiver in combination with the source IP address, the SCTP
            // destination port, and possibly the destination IP address to
            // identify the association to which this packet belongs.  The port
            // number 0 MUST NOT be used.
            if (p.getSrcPort() == 0)
            {
                throw new InvalidSCTPPacketException("errSCTPPacketSourcePortZero");
            }

            // This is the SCTP port number to which this packet is destined.
            // The receiving host will use this port number to de-multiplex the
            // SCTP packet to the correct receiving endpoint/application.  The
            // port number 0 MUST NOT be used.
            if (p.getDestPort() == 0)
            {
                throw new InvalidSCTPPacketException("errSCTPPacketDestinationPortZero");
            }

            foreach (var c in p.getChunkList())
            {
                switch (c.getType())
                {
                    case ChunkType.INIT:
                        {
                            if (p.getChunkList().Count != 1)
                            {
                                throw new InvalidSCTPPacketException("errInitChunkBundled");
                            }
                            if (p.getVerTag() != 0)
                            {
                                throw new InvalidSCTPPacketException("errInitChunkVerifyTagNotZero");
                            }
                        }
                        break;
                }
            }
        }

        void readLoop()
        {
            Association me = this;

            if (_rcv != null)
            {
                return;
            }

            _rcv = new Thread(() =>
            {
                this.Started = true;
                try
                {
                    byte[] buf = null;
                    while (_rcv != null)
                    {
                        try
                        {
                            buf = new byte[receiveMTU];
                            var length = _transp.Receive(buf, 0, buf.Length, TICK);
                            if (length > 0)
                            {
                                //var b = Packet.getHex(buf, 0, length);
                                //logger.LogInformation($"DTLS message recieved\n{b}");
                                //var inbound = new byte[length];

                                ByteBuffer pbb = new ByteBuffer(buf);
                                pbb.Limit = length;
                                Packet rec = new Packet(pbb);
                                handleInbound(rec);
                            }
                            else if (length == DtlsSrtpTransport.DTLS_RECEIVE_ERROR_CODE)
                            {
                                // The DTLS transport has been closed or i no longer available.
                                break;
                            }
                            else
                            {
                                //logger.LogInformation("Timeout -> short packet " + length);
                                //Thread.Sleep(1);
                            }
                        }
                        catch (SocketException e)
                        {
                            // ignore. it should be a timeout.
                            switch (e.SocketErrorCode)
                            {
                                case SocketError.TimedOut:
                                    logger.LogDebug("tick time out");
                                    break;
                                default:
                                    throw;
                            }
                        }
                    }
                    logger.LogDebug("SCTP message receive was empty, closing association listener.");

                    _transp.Close();
                }
                catch (EndOfStreamException eof)
                {
                    unexpectedClose(eof);
                    logger.LogDebug(eof.ToString());
                }
                catch (Exception ex)
                {
                    logger.LogDebug("Association receive failed " + ex.GetType().Name + " " + ex.ToString());
                }
                finally
                {
                    IsDone = true;
                }
            });
            _rcv.IsBackground = true;
            _rcv.Name = name + "_rcv";
            _rcv.Start();
        }

        void writeLoop()
        {
            Association me = this;

            if (_send != null)
            {
                return;
            }

            _send = new Thread(() =>
            {
                this.Started = true;
                try
                {
                    while (_rcv != null)
                    {
                        try
                        {
                            var rawPackets = gatherOutbound();
                            foreach (var obb in rawPackets)
                            {
                                var buf = obb.getByteBuffer();
                                _transp.Send(buf.Data, buf.offset, buf.Limit);
                            }
                        }
                        catch (SocketException e)
                        {
                            // ignore. it should be a timeout.
                            switch (e.SocketErrorCode)
                            {
                                case SocketError.TimedOut:
                                    logger.LogDebug("tick time out");
                                    break;
                                default:
                                    throw;
                            }
                        }
                        Thread.Sleep(1);
                    }
                    logger.LogDebug("SCTP message receive was empty, closing association listener.");

                    _transp.Close();
                }
                catch (EndOfStreamException eof)
                {
                    unexpectedClose(eof);
                    logger.LogDebug(eof.ToString());
                }
                catch (Exception ex)
                {
                    logger.LogDebug("Association receive failed " + ex.GetType().Name + " " + ex.ToString());
                }
                finally
                {
                    IsDone = true;
                }
            });
            _send.IsBackground = true;
            _send.Name = name + "_send";
            _send.Start();
        }

        private List<Packet> gatherOutbound()
        {
            var rawPackets = new List<Packet>();
            lock (myLock)
            {
                if (controlQueue.size() > 0)
                {
                    var chunks = controlQueue.popAll();
                    foreach (var c in chunks)
                    {
                        rawPackets.Add(c);
                    }
                }

                var state = this._state;
                if (state == State.ESTABLISHED)
                {
                    rawPackets.AddRange(gatherOutboundDataAndReconfigPackets());
                    rawPackets.AddRange(gatherOutboundFastRetransmissionPackets());

                    rawPackets.AddRange(gatherOutboundSackPackets());
                    rawPackets.AddRange(gatherOutboundForwardTSNPackets());
                }
            }
            return rawPackets;
        }

        // The caller should hold the lock
        Packet[] gatherOutboundFastRetransmissionPackets()
        { 
	        if (willRetransmitFast)
            {
                willRetransmitFast = false;

                var toFastRetrans = new List<Chunk>();
                uint fastRetransSize = commonHeaderSize;

		        for (uint i = 0; ; i++)
                {
			        if (!inflightQueue.get(cumulativeTSNAckPoint + i + 1, out var c))
                    {
                        break; // end of pending data
			        }

			        if (c.acked || c.abandoned())
                    {
                        continue;
			        }

			        if (c.nSent > 1 || c.missIndicator < 3)
                    {
                        continue;
			        }

                    // RFC 4960 Sec 7.2.4 Fast Retransmit on Gap Reports
                    //  3)  Determine how many of the earliest (i.e., lowest TSN) DATA chunks
                    //      marked for retransmission will fit into a single packet, subject
                    //      to constraint of the path MTU of the destination transport
                    //      address to which the packet is being sent.  Call this value K.
                    //      Retransmit those K DATA chunks in a single packet.  When a Fast
                    //      Retransmit is being performed, the sender SHOULD ignore the value
                    //      of cwnd and SHOULD NOT delay retransmission for this single
                    //		packet.

                    var dataChunkSize = AssociationConsts.dataChunkHeaderSize + c.getDataSize();
			        if (mtu < fastRetransSize+dataChunkSize)
                    {
                        break;
			        }

                    fastRetransSize += dataChunkSize;
                    stats.incFastRetrans();
                    c.nSent++;
                    checkPartialReliabilityStatus(c);
                    toFastRetrans.Add(c);
                    logger.LogTrace($"{name} fast-retransmit: tsn={c.tsn} sent={c.nSent} htna={fastRecoverExitPoint}");
		        }

		        if (toFastRetrans.Count > 0)
                {
                    return new Packet[] { makePacket(toFastRetrans.ToArray()) };
		        }
	        }

            return new Packet[0];
        }

        IEnumerable<Packet> gatherOutboundForwardTSNPackets()
        {
            if (willSendForwardTSN)
            {
                willSendForwardTSN = false;
                if (Utils.sna32GT(advancedPeerTSNAckPoint, cumulativeTSNAckPoint))
                {
                    var fwdtsn = createForwardTSN();
                    yield return makePacket(fwdtsn);
                }
            }
        }

        // createForwardTSN generates ForwardTSN chunk.
        // This method will be be called if useForwardTSN is set to false.
        // The caller should hold the lock.
        Chunk[] createForwardTSN()
        {
            // RFC 3758 Sec 3.5 C4
            var streamMap = new Dictionary<int, int>(); // to report only once per SI
            for (uint i = cumulativeTSNAckPoint + 1; Utils.sna32LTE(i, advancedPeerTSNAckPoint); i++)
            {
                if (!inflightQueue.get(i, out var c))
                {
                    break;
                }

                if (!streamMap.TryGetValue(c.getStreamId(), out var ssn))
                {
                    streamMap.Add(c.getStreamId(), c.getSSeqNo());
                }
                else
                {
                    streamMap[c.getStreamId()] = c.getSSeqNo();
                }
            }
            return new Chunk[0];
        }

        ////                var ssn = streamMap.Contains(c.getStreamId());
        ////    ssn, ok := streamMap[c.streamIdentifier]
        ////		if !ok {
        ////			streamMap[c.streamIdentifier] = c.streamSequenceNumber
        ////} else if sna16LT(ssn, c.streamSequenceNumber) {
        ////    // to report only once with greatest SSN
        ////    streamMap[c.streamIdentifier] = c.streamSequenceNumber

        //        }
        //	}

        //	//fwdtsn:= &chunkForwardTSN{
        ////newCumulativeTSN: a.advancedPeerTSNAckPoint,
        ////		streams:[]chunkForwardTSNStream{ },
        //	}

        ////var streamStr string
        ////	for si, ssn := range streamMap
        ////{
        ////    streamStr += fmt.Sprintf("(si=%d ssn=%d)", si, ssn)

        ////        fwdtsn.streams = append(fwdtsn.streams, chunkForwardTSNStream{
        ////identifier: si,
        ////			sequence: ssn,
        ////		})
        ////	}
        ////a.log.Tracef("[%s] building fwdtsn: newCumulativeTSN=%d cumTSN=%d - %s", a.name, fwdtsn.newCumulativeTSN, a.cumulativeTSNAckPoint, streamStr)


        ////    return fwdtsn
        //}






        private Packet[] gatherOutboundDataAndReconfigPackets()
        {
            var packets = new List<Packet>();
            packets.AddRange(getDataPacketsToRetransmit());

            var chunks = popPendingDataChunksToSend(out var sisToReset);
            if (chunks.Length > 0)
            {
                packets.AddRange(bundleDataChunksIntoPackets(chunks));
            }

            if (sisToReset.Count > 0 || willRetransmitReconfig)
            {
                if (willRetransmitReconfig)
                {
                    willRetransmitReconfig = false;
                    packets.Add(makePacket(reconfigs.Values.ToArray()));
                }

                if (sisToReset.Count > 0)
                {
                    var rsn = generateNextRSN();
                    var tsn = myNextTSN - 1;
                    var c = new ReConfigChunk();
                    OutgoingSSNResetRequestParameter rep = new OutgoingSSNResetRequestParameter(rsn, rsn, tsn);
                    rep.setStreams(sisToReset.ToArray());
                    c.addParam(rep);

                    if (!reconfigs.ContainsKey(rsn))
                    {
                        reconfigs.Add(rsn, c);
                    }
                    else
                    {
                        reconfigs[rsn] = c;
                    }
                    packets.Add(makePacket(c));
                }

                if (reconfigs.Count > 0)
                {
                    tReconfig.start(rtoMgr.getRTO());
                }
            }

            return packets.ToArray();
        }

        uint generateNextRSN()
        {
            var rsn = myNextRSN;
            myNextRSN++;
            return rsn;
        }

        private IEnumerable<Packet> gatherOutboundSackPackets()
        {
            if (ackState == AcknowlegeState.Immediate)
            {
                ackState = AcknowlegeState.Idle;
                var sack = createSelectiveAckChunk();
                logger.LogTrace($"{name} sending SACK: {sack}");
                yield return makePacket(sack);
            }
        }

        private SackChunk createSelectiveAckChunk()
        {
            var sack = new SackChunk();
            sack.cumulativeTSNAck = peerLastTSN;
            sack.advertisedReceiverWindowCredit = getMyReceiverWindowCredit();
            sack.duplicateTSN = payloadQueue.popDuplicates();
            sack.gapAckBlocks = payloadQueue.getGapAckBlocks(peerLastTSN);
            return sack;
        }

        private Packet[] getDataPacketsToRetransmit()
        {
            var awnd = min32(_cwnd, _rwnd);
            var chunks = new List<DataChunk>();
            uint bytesToSend = 0;
            var done = false;
            for (uint i = 0; !done; i++)
            {
                if (!inflightQueue.get(cumulativeTSNAckPoint + i + 1, out var c))
                    break;
                if (!c.retransmit)
                    continue;

                if (i == 0 && _rwnd < c.getDataSize())
                {
                    // Send it as a zero window probe
                    done = true;
                }
                else if (bytesToSend + c.getDataSize() > awnd)
                {
                    break;
                }

                // reset the retransmit flag not to retransmit again before the next
                // t3-rtx timer fires
                c.retransmit = false;

                bytesToSend += c.getDataSize();
                c._retryCount++;

                checkPartialReliabilityStatus(c);

                logger.LogTrace($"{name} retransmitting tsn={c.tsn} ssn={c.streamSequenceNumber} sent={c.nSent}");

                chunks.Add(c);
            }
            return bundleDataChunksIntoPackets(chunks);
        }

        private DataChunk[] popPendingDataChunksToSend(out List<int> sisToReset)
        {
            var chunks = new List<DataChunk>();
            sisToReset = new List<int>();

            if (pendingQueue.size() > 0)
            {
                while (true)
                {
                    var c = pendingQueue.peek();
                    if (c == null)
                    {
                        break;
                    }

                    var dataLen = c.getDataSize();
                    if (dataLen == 0)
                    {
                        sisToReset.Add(c.getStreamId());
                        pendingQueue.pop(c);
                        continue;
                    }

                    if (this.inflightQueue.getNumBytes() + dataLen > _cwnd)
                    {
                        break; // would exceeds cwnd
                    }

                    if (dataLen > _rwnd)
                    {
                        break; // no more rwnd
                    }

                    _rwnd -= dataLen;
                    movePendingDataChunkToInflightQueue(c);
                    chunks.Add(c);
                }
            }
            return chunks.ToArray();
        }

        private void movePendingDataChunkToInflightQueue(DataChunk c)
        {
            pendingQueue.pop(c);

            if (c.endingFragment)
            {
                c.setAllInflight();
            }

            var now = TimeExtension.CurrentTimeMillis();
            // Assign TSN
            c.setTsn(generateNextTSN());
            c.setGapAck(false);
            c.setRetryTime(now + getT3() - 1);
            c.setSentTime(now); // use to calculate RTT and also for maxPacketLifeTime
            c._retryCount = 1;          // being sent for the first time
            checkPartialReliabilityStatus(c);

            //    long now = TimeExtension.CurrentTimeMillis();
            //    d.setTsn(_nearTSN++);
            //    d.setGapAck(false);
            //    d.setRetryTime(now + getT3() - 1);
            //    d.setSentTime(now);
            //_timer.setRunnable(this, getT3());

            // Push it into the inflightQueue
            this.inflightQueue.pushNoCheck(c);
        }

        public long getT3()
        {
            return (_rto > 0) ? (long)(1000.0 * _rto) : 100;
        }

        // bundleDataChunksIntoPackets packs DATA chunks into packets. It tries to bundle
        // DATA chunks into a packet so long as the resulting packet size does not exceed
        // the path MTU.
        // The caller should hold the lock.
        private Packet[] bundleDataChunksIntoPackets(IEnumerable<DataChunk> chunkPayloadData)
        {
            var packets = new List<Packet>();
            var chunksToSend = new List<DataChunk>();
            uint bytesInPacket = (uint)commonHeaderSize;
            foreach (var c in chunkPayloadData)
            {
                if (bytesInPacket + c.getDataSize() > mtu)
                {
                    packets.Add(makePacket(chunksToSend.ToArray()));
                    chunksToSend = new List<DataChunk>();
                    bytesInPacket = commonHeaderSize;
                }


                chunksToSend.Add(c);

                bytesInPacket += commonHeaderSize + c.getDataSize();
            }

            if (chunksToSend.Count > 0)
            {
                packets.Add(makePacket(chunksToSend.ToArray()));
            }

            return packets.ToArray();
        }
        //        func(a* Association) bundleDataChunksIntoPackets(chunks[]*chunkPayloadData) []* packet
        //        {
        //            packets := []*packet { }
        //            chunksToSend := []
        //            chunk { }
        //            bytesInPacket := int (commonHeaderSize)

        //	for _, c := range chunks
        //        {
        //		// RFC 4960 sec 6.1.  Transmission of DATA Chunks
        //		//   Multiple DATA chunks committed for transmission MAY be bundled in a
        //		//   single packet.  Furthermore, DATA chunks being retransmitted MAY be
        //		//   bundled with new DATA chunks, as long as the resulting packet size
        //		//   does not exceed the path MTU.
        //		if bytesInPacket+len(c.userData) > int (a.mtu) {
        //			packets = append(packets, a.createPacket(chunksToSend))

        //            chunksToSend = [] chunk{}
        //    bytesInPacket = int (commonHeaderSize)
        //}

        //chunksToSend = append(chunksToSend, c)

        //        bytesInPacket += int(dataChunkHeaderSize) + len(c.userData)
        //	}

        //	if len(chunksToSend) > 0 {
        //    packets = append(packets, a.createPacket(chunksToSend))

        //    }

        //return packets
        //}

        private void checkPartialReliabilityStatus(DataChunk c)
        {
            if (!useForwardTSN)
            {
                return;
            }

            if (c.payloadType == DataChunk.WEBRTCCONTROL)
            {
                return;
            }

            if (_streams.TryGetValue(c.getStreamId(), out var s))
            {
                lock(s.rwLock)
                {
                    if (s.reliabilityType == ReliabilityType.TypeRexmit)
                    {
                        if (c._retryCount >= s.reliabilityValue)
                        {
                            c.setAbandoned(true);
                            logger.LogTrace($"{name} marked as abandoned: tsn={c.tsn} ppi={c.payloadType} (remix: {c.nSent})");
                        }
                    }
                    else if (s.reliabilityType == ReliabilityType.TypeTimed)
                    {
                        long now = TimeExtension.CurrentTimeMillis();
                        var elapsed = now - c.getSentTime();
                        if (elapsed > s.reliabilityValue)
                        {
                            c.setAbandoned(true);
                            logger.LogTrace($"{name} marked as abandoned: tsn={c.tsn} ppi={c.payloadType} (timed: {elapsed})");
                        }
                    }
                }
            }
            else
            {
                logger.LogError($"{name} stream {c.streamIdentifier} not found)"); 
            }
        }


        //        func (a *Association) gatherOutboundDataAndReconfigPackets(rawPackets [][]byte) [][]byte {
        //	for _, p := range a.getDataPacketsToRetransmit() {
        //		raw, err := p.marshal()
        //		if err != nil {
        //			a.log.Warnf("[%s] failed to serialize a DATA packet to be retransmitted", a.name)
        //			continue
        //		}
        //		rawPackets = append(rawPackets, raw)
        //	}

        //	// Pop unsent data chunks from the pending queue to send as much as
        //	// cwnd and rwnd allow.
        //	chunks, sisToReset := a.popPendingDataChunksToSend()
        //	if len(chunks) > 0 {
        //		// Start timer. (noop if already started)
        //		a.log.Tracef("[%s] T3-rtx timer start (pt1)", a.name)
        //		a.t3RTX.start(a.rtoMgr.getRTO())
        //		for _, p := range a.bundleDataChunksIntoPackets(chunks) {
        //			raw, err := p.marshal()
        //			if err != nil {
        //				a.log.Warnf("[%s] failed to serialize a DATA packet", a.name)
        //				continue
        //			}
        //			rawPackets = append(rawPackets, raw)
        //		}
        //	}

        //	if len(sisToReset) > 0 || a.willRetransmitReconfig {
        //		if a.willRetransmitReconfig {
        //			a.willRetransmitReconfig = false
        //			a.log.Debugf("[%s] retransmit %d RECONFIG chunk(s)", a.name, len(a.reconfigs))
        //			for _, c := range a.reconfigs {
        //				p := a.createPacket([]chunk{c})
        //				raw, err := p.marshal()
        //				if err != nil {
        //					a.log.Warnf("[%s] failed to serialize a RECONFIG packet to be retransmitted", a.name)
        //				} else {
        //					rawPackets = append(rawPackets, raw)
        //				}
        //			}
        //		}

        //		if len(sisToReset) > 0 {
        //			rsn := a.generateNextRSN()
        //			tsn := a.myNextTSN - 1
        //			c := &chunkReconfig{
        //				paramA: &paramOutgoingResetRequest{
        //					reconfigRequestSequenceNumber: rsn,
        //					senderLastTSN:                 tsn,
        //					streamIdentifiers:             sisToReset,
        //				},
        //			}
        //			a.reconfigs[rsn] = c // store in the map for retransmission
        //			a.log.Debugf("[%s] sending RECONFIG: rsn=%d tsn=%d streams=%v",
        //				a.name, rsn, a.myNextTSN-1, sisToReset)
        //			p := a.createPacket([]chunk{c})
        //			raw, err := p.marshal()
        //			if err != nil {
        //				a.log.Warnf("[%s] failed to serialize a RECONFIG packet to be transmitted", a.name)
        //			} else {
        //				rawPackets = append(rawPackets, raw)
        //			}
        //		}

        //		if len(a.reconfigs) > 0 {
        //			a.tReconfig.start(a.rtoMgr.getRTO())
        //		}
        //	}

        //	return rawPackets
        //}

        /**
		 * override this and return false to disable the bi-directionalinit gamble
		 * that webRTC expects. Only do this in testing. Production should have it
		 * enabled since it also provides glare resolution.
		 *
		 * @return true
		 */
        public bool doBidirectionalInit()
        {
            return true;
        }

        protected uint min32(uint a, uint b)
        {
            if (a < b)
            {
                return a;
            }
            return b;
        }
        protected uint max32(uint a, uint b)
        {
            if (a > b)
            {
                return a;
            }
            return b;
        }
        //protected bool sna32LT(uint i1, uint i2)
        //{
        //    return (i1 < i2 && i2 - i1 < 1 << 31) || (i1 > i2 && i1 - i2 > 1 << 31);
        //}

        protected void sendPayloadData(DataChunk[] chunks)
        {
            lock (myLock)
            {
                var state = _state;
                if (state != State.ESTABLISHED)
                {
                    throw new Exception($"errPayloadDataStateNotExist: state={state}");
                }

                if (chunks?.Length == 0)
                {
                    return;
                }

                //ByteBuffer obb = mkPkt(c);
                foreach (var c in chunks)
                {
                    pendingQueue.push(c);
                }
                //logger.LogDebug($"SCTP packet send: {Packet.getHex(obb)}");
            }
            //else
            //{
            //    logger.LogDebug("Blocked empty packet send() - probably no response needed.");
            //}
        }

        /**
		 * decide if we want to do the webRTC specified bidirectional init _very_
		 * useful to be able to switch this off for testing
		 *
		 * @return
		 */
        private bool acceptableStateForInboundInit()
        {
            bool ret = false;
            if (doBidirectionalInit())
            {
                ret = ((_state == State.CLOSED) || (_state == State.COOKIEWAIT) || (_state == State.COOKIEECHOED));
            }
            else
            {
                ret = (_state == State.CLOSED);
            }
            return ret;
        }

        /**
		 *
		 * @param c - Chunk to be processed
		 * @return valid - false if the remaining chunks of the packet should be
		 * ignored.
		 * @throws IOException
		 * @throws SctpPacketFormatException
		 */
        private void handleChunk(Packet p, Chunk c)
        {
            try
            {
                RWMutex.WaitOne();
                ChunkType ty = c.getType();
                //bool ret = true;
                State oldState = _state;
                Packet[] reply = null;
                switch (ty)
                {
                    case ChunkType.INIT:
                        //if (acceptableStateForInboundInit())
                        //{
                        InitChunk init = (InitChunk)c;
                        reply = handleInit(p, init);
                        //}
                        //else
                        //{
                        // logger.LogDebug("Got an INIT when state was " + _state.ToString() + " - ignoring it for now ");
                        //}
                        break;
                    case ChunkType.INITACK:
                        logger.LogDebug("got initack " + c.ToString());
                        if (_state == State.COOKIEWAIT)
                        {
                            InitAckChunk iack = (InitAckChunk)c;
                            reply = handleInitAck(p, iack);
                        }
                        else
                        {
                            logger.LogDebug("Got an INITACK when not waiting for it - ignoring it");
                        }
                        break;
                    case ChunkType.ABORT:
                        // no reply we should just bail I think.
                        _rcv = null;
                        _send = null;
                        _transp.Close();
                        break;
                    case ChunkType.ERROR:
                        logger.LogWarning($"SCTP error chunk received.");
                        foreach (var vparam in c._varList)
                        {
                            if (vparam is KnownError)
                            {
                                var knownErr = vparam as KnownError;
                                logger.LogWarning($"{knownErr.getName()}, {knownErr}");
                            }
                        }
                        break;
                    case ChunkType.HEARTBEAT:
                        reply = new Packet[] { makePacket(((HeartBeatChunk)c).mkReply()) };
                        break;
                    case ChunkType.COOKIE_ECHO:
                        logger.LogTrace("got cookie echo " + c.ToString());
                        reply = new Packet[] { makePacket(cookieEchoDeal((CookieEchoChunk)c)) };
                        //if (reply.Length > 0)
                        //{
                        //    ret = !typeof(ErrorChunk).IsAssignableFrom(reply[0].GetType()); // ignore any following data chunk. 
                        //}
                        break;
                    case ChunkType.COOKIE_ACK:
                        logger.LogTrace("got cookie ack " + c.ToString());
                        if (_state == State.COOKIEECHOED)
                        {
                            t1Cookie.stop();
                            _state = State.ESTABLISHED;
                        }
                        break;
                    case ChunkType.DATA:
                        //logger.LogDebug("got data " + c.ToString());
                        var dc = (DataChunk)c;
                        //logger.LogDebug("SCTP received " + dc.ToString());
                        if (dc.getDCEP() != null)
                        {
                            var pkt = ingest(dc);
                            if (pkt != null)
                            {
                                reply = new Packet[] { pkt };
                            }
                        }
                        else
                        {
                            reply = handleData(dc);
                        }
                        break;
                    case ChunkType.SACK:
                        logger.LogTrace("got tsak for TSN " + ((SackChunk)c).cumulativeTSNAck);
                        handleSack((SackChunk)c);
                        // fix the outbound list here
                        break;
                    case ChunkType.RE_CONFIG:
                        reply = new Packet[] { makePacket(reconfigState.deal((ReConfigChunk)c)) };
                        break;
                }
                if (reply?.Length > 0)
                {
                    this.controlQueue.pushAll(reply);
                }
                if ((_state == State.ESTABLISHED) && (oldState != State.ESTABLISHED))
                {
                    if (null != _al)
                    {
                        _al.onAssociated(this);
                    }
                    reconfigState = new ReconfigState(this, peerLastTSN);
                }
                if ((oldState == State.ESTABLISHED) && (_state != State.ESTABLISHED))
                {
                    if (null != _al)
                    {
                        _al.onDisAssociated(this);
                    }
                }
                //return ret;
            }
            finally
            {
                RWMutex.ReleaseMutex();
            }
        }

        public void run()
        {

        }

        /*
 In instances where its peer endpoint is multi-homed, if an endpoint
 receives a SACK that advances its Cumulative TSN Ack Point, then it
 should update its cwnd (or cwnds) apportioned to the destination
 addresses to which it transmitted the acknowledged data.  However, if



 Stewart                     Standards Track                    [Page 96]

 RFC 4960          Stream Control Transmission Protocol    September 2007


 the received SACK does not advance the Cumulative TSN Ack Point, the
 endpoint MUST NOT adjust the cwnd of any of the destination
 addresses.

 Because an endpoint's cwnd is not tied to its Cumulative TSN Ack
 Point, as duplicate SACKs come in, even though they may not advance
 the Cumulative TSN Ack Point an endpoint can still use them to clock
 out new data.  That is, the data newly acknowledged by the SACK
 diminishes the amount of data now in flight to less than cwnd, and so
 the current, unchanged value of cwnd now allows new data to be sent.
 On the other hand, the increase of cwnd must be tied to the
 Cumulative TSN Ack Point advancement as specified above.  Otherwise,
 the duplicate SACKs will not only clock out new data, but also will
 adversely clock out more new data than what has just left the
 network, during a time of possible congestion.

 o  When the endpoint does not transmit data on a given transport
 address, the cwnd of the transport address should be adjusted to
 max(cwnd/2, 4*MTU) per RTO.

 */

        // timer goes off,
        //public void run()
        //{
        //    if (canSend())
        //    {
        //        long now = TimeExtension.CurrentTimeMillis();
        //        //logger.LogDebug("retry timer went off at " + now);
        //        List<DataChunk> dcs = new List<DataChunk>();
        //        int space = _transpMTU - 12; // room for packet header
        //        bool resetTimer = false;
        //        lock (_inFlight)
        //        {
        //            foreach (var kvp in _inFlight)
        //            {
        //                DataChunk d = kvp.Value;
        //                long k = kvp.Key;
        //                if (d.getGapAck())
        //                {
        //                    //logger.LogDebug("skipping gap-acked tsn " + d.getTsn());
        //                    continue;
        //                }
        //                if (d.getRetryTime() <= now)
        //                {
        //                    space -= d.getLength();
        //                    //logger.LogDebug("available space in pkt is " + space);
        //                    if (space <= 0)
        //                    {
        //                        resetTimer = true;
        //                        break;
        //                    }
        //                    else
        //                    {
        //                        dcs.Add(d);
        //                        d.setRetryTime(now + getT3() - 1);
        //                    }
        //                }
        //                else
        //                {
        //                    //logger.LogDebug("retry not yet due for  " + d.ToString());
        //                    resetTimer = true;
        //                }
        //            }
        //        }
        //        if (dcs.Count != 0)
        //        {
        //            dcs.Sort();
        //            DataChunk[] da = new DataChunk[dcs.Count];
        //            int i = 0;
        //            foreach (DataChunk d in dcs)
        //            {
        //                da[i++] = d;
        //            }
        //            resetTimer = true;
        //            try
        //            {
        //                //logger.LogDebug("Sending retry for  " + da.Length + " data chunks");
        //                this.send(da);
        //            }
        //            catch (EndOfStreamException end)
        //            {
        //                logger.LogWarning("Retry send failed " + end.ToString());
        //                unexpectedClose(end);
        //                resetTimer = false;
        //            }
        //            catch (Exception ex)
        //            {
        //                logger.LogError("Cant send retry - eek " + ex.ToString());
        //            }
        //        }
        //        else
        //        {
        //            //logger.LogDebug("Nothing to do ");
        //        }
        //        if (resetTimer)
        //        {
        //            _timer.setRunnable(this, getT3());
        //            //logger.LogDebug("Try again in a while  " + getT3());

        //        }
        //    }
        //}

        public ByteBuffer mkPkt(params Chunk[] cs)
        {
            Packet ob = new Packet(_srcPort, _destPort, peerVerificationTag);
            foreach (Chunk r in cs)
            {
                //logger.LogDebug("adding chunk to outbound packet: " + r.ToString());
                ob.Add(r);
                //todo - this needs to workout if all the chunks will fit...
            }
            ByteBuffer obb = ob.getByteBuffer();
            return obb;
        }

        public Packet makePacket(params Chunk[] cs)
        {
            Packet ob = new Packet(_srcPort, _destPort, peerVerificationTag);
            foreach (Chunk r in cs)
            {
                //logger.LogDebug("adding chunk to outbound packet: " + r.ToString());
                ob.Add(r);
                //todo - this needs to workout if all the chunks will fit...
            }
            return ob;
        }

        //public Packet makePacket(IEnumerable<Chunk> cs)
        //{
        //    Packet ob = new Packet(_srcPort, _destPort, peerVerificationTag);
        //    foreach (Chunk r in cs)
        //    {
        //        //logger.LogDebug("adding chunk to outbound packet: " + r.ToString());
        //        ob.Add(r);
        //        //todo - this needs to workout if all the chunks will fit...
        //    }
        //    return ob;
        //}

        public uint getPeerVerTag()
        {
            return peerVerificationTag;
        }

        public uint getMyVerTag()
        {
            return myVerificationTag;
        }

        /*
		 Ok - confession here - we are not following the RFC. 
		 We don't encode a pile of stuff into the cookie and decode it
		 when we get the cookie back, then use that data to initialize the Association.
		 The rationale in the RFC is to protect the assocaition from resource exhaustion
		 by fake cookies from bad guys - which makes sense if you are a naked SCTP stack on
		 the internet accepting UDP packets (or IP ones).
		 We on the other hand have already been through 2 levels of validation with ICE and DTLS,
		 and have already committed a pile of resource to this connection, so 32 bytes more won't kill us.
    
		 The only downside is that if the far end spams us with a pile of inits at speed, we may erase one that we've
		 replied to and that was about to be a happy camper. Shrug.
		 */
        private CookieHolder checkCookieEcho(byte[] cookieData)
        {
            CookieHolder same = null;
            foreach (CookieHolder cookie in _cookies)
            {
                byte[] cd = cookie.cookieData;
                if (cd.Length == cookieData.Length)
                {
                    int i = 0;
                    while (i < cd.Length)
                    {
                        if (cd[i] != cookieData[i])
                        {
                            break;
                        }
                        i++;
                    }
                    if (i == cd.Length)
                    {
                        same = cookie;
                        break;
                    }
                }
            }
            return same;
        }

        private uint howStaleIsMyCookie(CookieHolder cookie)
        {
            uint ret = 0;
            long now = TimeExtension.CurrentTimeMillis();

            if ((now - cookie.cookieTime) < VALIDCOOKIELIFE)
            {
                ret = 0;
            }
            else
            {
                ret = (uint)((now - cookie.cookieTime) - VALIDCOOKIELIFE);
            }
            return ret;
        }

        public void sendInit()
        {
            try
            {
                if (storedInit == null)
                {
                    throw new Exception("errInitNotStoredToSend");
                }

                var outbound = new Packet(_srcPort, _destPort, peerVerificationTag);
                outbound.Add(storedInit);
                this.controlQueue.push(outbound);
            }
            finally
            {
                //RWMutex.ReleaseMutex();
            }
        }

        void sendCookieEcho()
        {
            if (storedCookieEcho == null)
            {
                throw new Exception("errCookieEchoNotStoredToSend");
            }

            logger.LogDebug($"{name} sending COOKIE-ECHO");

            //   var outbound = new Packet();
            //outbound.verificationTag = peerVerificationTag
            //outbound.sourcePort = sourcePort
            //outbound.destinationPort = destinationPort''
            //outbound.chunks = []chunk{a.storedCookieEcho}

            controlQueue.push(makePacket(storedCookieEcho));
            //a.awakeWriteLoop()
        }

        protected virtual Packet[] handleInitAck(Packet p, InitAckChunk i)
        {
            var state = _state;
            if (state != State.COOKIEWAIT)
            {
                // RFC 4960
                // 5.2.3.  Unexpected INIT ACK
                //   If an INIT ACK is received by an endpoint in any state other than the
                //   COOKIE-WAIT state, the endpoint should discard the INIT ACK chunk.
                //   An unexpected INIT ACK usually indicates the processing of an old or
                //   duplicated INIT chunk.
                return null;
            }
            myMaxNumInboundStreams = min16((ushort)i.getNumInStreams(), myMaxNumInboundStreams);
            myMaxNumOutboundStreams = min16((ushort)i.getNumOutStreams(), myMaxNumOutboundStreams);
            peerVerificationTag = i.getInitiateTag();
            peerLastTSN = i.getInitialTSN() - 1;
            if ((_srcPort != p.getDestPort()) || (_destPort != p.getSrcPort()))
            {
                logger.LogWarning($"{name} handleInitAck: port mismatch");
                return null;
            }
            _rwnd = i.getAdRecWinCredit();
            _ssthresh = _rwnd;
            t1Init.stop();
            storedInit = null;
            /* 
			 NOTE: TO DO - this is a protocol violation - this should be done with
			 multiple TCBS and set in cookie echo 
			 NOT HERE
			 */
            i.getSupportedExtensions(_supportedExtensions);

            byte[] data = i.getCookie();
            CookieEchoChunk ce = new CookieEchoChunk();
            ce.setCookieData(data);
            storedCookieEcho = ce;

            sendCookieEcho();

            t1Cookie.start(rtoMgr.getRTO());
            this._state = State.COOKIEECHOED;
            return null;
        }

        /* <pre>
		 5.2.1.  INIT Received in COOKIE-WAIT or COOKIE-ECHOED State (Item B)

		 This usually indicates an initialization collision, i.e., each
		 endpoint is attempting, at about the same time, to establish an
		 association with the other endpoint.

		 Upon receipt of an INIT in the COOKIE-WAIT state, an endpoint MUST
		 respond with an INIT ACK using the same parameters it sent in its
		 original INIT chunk (including its Initiate Tag, unchanged).  When
		 responding, the endpoint MUST send the INIT ACK back to the same
		 address that the original INIT (sent by this endpoint) was sent.

		 Upon receipt of an INIT in the COOKIE-ECHOED state, an endpoint MUST
		 respond with an INIT ACK using the same parameters it sent in its
		 original INIT chunk (including its Initiate Tag, unchanged), provided
		 that no NEW address has been added to the forming association.  If
		 the INIT message indicates that a new address has been added to the
		 association, then the entire INIT MUST be discarded, and NO changes
		 should be made to the existing association.  An ABORT SHOULD be sent
		 in response that MAY include the error 'Restart of an association
		 with new addresses'.  The error SHOULD list the addresses that were
		 added to the restarting association.

		 When responding in either state (COOKIE-WAIT or COOKIE-ECHOED) with
		 an INIT ACK, the original parameters are combined with those from the
		 newly received INIT chunk.  The endpoint shall also generate a State
		 Cookie with the INIT ACK.  The endpoint uses the parameters sent in
		 its INIT to calculate the State Cookie.

		 After that, the endpoint MUST NOT change its state, the T1-init timer
		 shall be left running, and the corresponding TCB MUST NOT be
		 destroyed.  The normal procedures for handling State Cookies when a
		 TCB exists will resolve the duplicate INITs to a single association.

		 For an endpoint that is in the COOKIE-ECHOED state, it MUST populate
		 its Tie-Tags within both the association TCB and inside the State
		 Cookie (see Section 5.2.2 for a description of the Tie-Tags).
		 </pre>
		 */
        public virtual Packet[] handleInit(Packet p, InitChunk i)
        {
            var state = _state;
            // https://tools.ietf.org/html/rfc4960#section-5.2.1
            // Upon receipt of an INIT in the COOKIE-WAIT state, an endpoint MUST
            // respond with an INIT ACK using the same parameters it sent in its
            // original INIT chunk (including its Initiate Tag, unchanged).  When
            // responding, the endpoint MUST send the INIT ACK back to the same
            // address that the original INIT (sent by this endpoint) was sent.

            if (state != State.CLOSED && state != State.COOKIEWAIT && state != State.COOKIEECHOED)
            {
                // 5.2.2.  Unexpected INIT in States Other than CLOSED, COOKIE-ECHOED,
                //        COOKIE-WAIT, and SHUTDOWN-ACK-SENT
                throw new Exception($"errHandleInitState {state}");
            }
            myMaxNumInboundStreams = min16((ushort)i.getNumInStreams(), myMaxNumInboundStreams);
            myMaxNumOutboundStreams = min16((ushort)i.getNumOutStreams(), myMaxNumOutboundStreams);
            peerVerificationTag = i.getInitiateTag();
            _srcPort = p.getSrcPort();
            _destPort = p.getDestPort();
            _rwnd = i.getAdRecWinCredit();
            peerLastTSN = (uint)(i.getInitialTSN() - 1);

            this.useForwardTSN = i._farForwardTSNsupported;
            if (!useForwardTSN)
            {
                logger.LogWarning("not using ForwardTSN (on init)");
            }

            var outbound = new Packet(_srcPort, _destPort, peerVerificationTag);


            var iac = new InitAckChunk();
            iac.setInitialTSN(myNextTSN);
            iac.setAdRecWinCredit(maxReceiveBufferSize);
            iac.setNumInStreams(myMaxNumInboundStreams);
            iac.setNumOutStreams(myMaxNumOutboundStreams);
            iac.setInitiateTag(myVerificationTag);

            if (myCookie == null)
            {
                var cookie = new CookieHolder();
                cookie.cookieData = new byte[Association.COOKIESIZE];
                cookie.cookieTime = TimeExtension.CurrentTimeMillis();
                _random.NextBytes(cookie.cookieData);
                _cookies.Add(cookie);
                myCookie = cookie;
            }
            iac.setCookie(myCookie.cookieData);

            byte[] fse = i.getFarSupportedExtensions();
            if (fse != null)
            {
                iac.setSupportedExtensions(this.getUnionSupportedExtensions(fse));
            }
            outbound.Add(iac);
            //var reply = new Packet(mkPkt(iac));
            //logger.LogDebug("SCTP received INIT:" + init.ToString());
            //logger.LogDebug("Replying with init-ack :" + iac.ToString());
            return new Packet[] { outbound };
        }

        ushort min16(ushort a, ushort b)
        {
            if (a < b)
            {
                return a;
            }
            return b;
        }

        public SCTPStream getOrCreateStream(int sno)
        {
            SCTPStream _in;
            if (!_streams.TryGetValue(sno, out _in))
            {
                _in = mkStream(sno);
                _streams.TryAdd(sno, _in);
                _al.onRawStream(_in);
            }
            return _in;
        }

        private Packet ingest(DataChunk dc)
        {            
            Chunk closer = null;
            int sno = dc.getStreamId();
            uint tsn = dc.getTsn();
            SCTPStream _in;
            if (!_streams.TryGetValue(sno, out _in))
            {
                _in = mkStream(sno);
                _streams.TryAdd(sno, _in);
                _al.onRawStream(_in);
            }
            var repa = new List<Chunk>();
            // todo dcep logic belongs in behave - not here.
            if (dc.getDCEP() != null)
            {
                var chunks = dcepDeal(_in, dc, dc.getDCEP());
                if (chunks?.Length > 0)
                {
                    repa.AddRange(chunks);
                }
                // delay 'till after first packet so we can get the label etc set 
                // _however_ this should be in behave -as mentioned above.
                try
                {
                    _al.onDCEPStream(_in, _in.getLabel(), dc.getPpid());
                    if (_in.OnOpen != null) _in.OnOpen.Invoke();
                }
                catch (Exception x)
                {
                    closer = _in.immediateClose();
                    logger.LogError(x.ToString());
                }
            }
            //else
            //{
            //    repa = _in.append(dc);
            //}

            if (closer != null)
            {
                repa.Add(closer);
            }
            //_in.handleData(dc);
            peerLastTSN = tsn;
            if (repa.Count == 0)
            {
                return null;
            }
            payloadQueue.push(dc, peerLastTSN);
            return makePacket(repa.ToArray());
        }

        private Packet[] handleData(DataChunk d)
        {
            List<Chunk> rep = new List<Chunk>();

            var canPush = payloadQueue.canPush(d, peerLastTSN);
            if (canPush)
            {
                var s = getOrCreateStream(d.streamIdentifier);

                if (getMyReceiverWindowCredit() > 0)
                {
                    payloadQueue.push(d, peerLastTSN);
                    s.handleData(d);
                }
                else
                {
                    if (payloadQueue.getLastTSNReceived(out var lastTSN) && Utils.sna32LT(d.getTsn(), lastTSN))
			        {
                        logger.LogDebug($"{name} receive buffer full, but accepted as this is a missing chunk with tsn={d.getTsn()} ssn=%d");//, d.streamSequenceNumber)
                        payloadQueue.push(d, peerLastTSN);
                        s.handleData(d);
			        } 
                    else 
                    {
                        logger.LogDebug($"{name}  receive buffer full. dropping DATA with tsn={d.getTsn()} ssn=%d");// d.streamSequenceNumber)
			        }
                }
            }
            //var sack = mkSack();
            //rep.Add(sack);
            //return rep.ToArray();

            return handlePeerLastTSNAndAcknowledgement(d.immediateSack);
        }

        // The caller should hold the lock.
        //Packet[] handleData(DataChunk d)
        //{
        //    //a.log.Tracef("[%s] DATA: tsn=%d immediateSack=%v len=%d",
        //    // a.name, d.tsn, d.immediateSack, len(d.userData))
        //    //a.stats.incDATAs()

        //    var canPush = payloadQueue.canPush(d, peerLastTSN);
        // if (canPush)
        //    {
        //        var s = getOrCreateStream(d.getStreamId());
        //  if (s == null)
        //        {
        //            // silentely discard the data. (sender will retry on T3-rtx timeout)
        //            // see pion/sctp#30
        //            //a.log.Debugf("discard %d", d.streamSequenceNumber)
        //            return null;
        //  }

        //  if (getMyReceiverWindowCredit() > 0)
        //        {
        //            // Pass the new chunk to stream level as soon as it arrives
        //            payloadQueue.push(d, peerLastTSN);
        //            s.handleData(d);
        //  }
        //        else
        //        {
        //   // Receive buffer is full
        //   var ok = payloadQueue.getLastTSNReceived(out var lastTSN);
        //   if (ok && Utils.sna32LT(d.getTsn(), lastTSN))
        //            {
        //                //a.log.Debugf("[%s] receive buffer full, but accepted as this is a missing chunk with tsn=%d ssn=%d", a.name, d.tsn, d.streamSequenceNumber)
        //                payloadQueue.push(d, peerLastTSN);
        //                s.handleData(d);
        //   }
        //            else
        //            {
        //    //a.log.Debugf("[%s] receive buffer full. dropping DATA with tsn=%d ssn=%d", a.name, d.tsn, d.streamSequenceNumber)
        //   }
        //  }
        // }
        //    return handlePeerLastTSNAndAcknowledgement(d.immediateSack);
        //}

        //// getOrCreateStream gets or creates a stream. The caller should hold the lock.
        //SCTPStream getOrCreateStream(int streamIdentifier)
        //{
        //    if (_streams.TryGetValue(streamIdentifier, out var s))
        //        return s;

        //    return createStream(streamIdentifier, true);
        //}

        //// createStream creates a stream. The caller should hold the lock and check no stream exists for this id.
        //SCTPStream createStream(int streamIdentifier, bool accept)
        //{
        //    var s = mkStream(streamIdentifier);
        //    _streams.Add(streamIdentifier, s);
        //    return s;
        //}

        // A common routine for handleData and handleForwardTSN routines
        // The caller should hold the lock.
        Packet[] handlePeerLastTSNAndAcknowledgement(bool sackImmediately)
        {
            var reply = new List<Packet>();

            // Try to advance peerLastTSN

            // From RFC 3758 Sec 3.6:
            //   .. and then MUST further advance its cumulative TSN point locally
            //   if possible
            // Meaning, if peerLastTSN+1 points to a chunk that is received,
            // advance peerLastTSN until peerLastTSN+1 points to unreceived chunk.
            while (true)
            {
                if (!payloadQueue.pop(peerLastTSN + 1, out var c))
                {
                    break;
                }
                peerLastTSN++;

                foreach (var rstReq in reconfigRequests.Values)
                {
                    var resp = resetStreamsIfAny(rstReq);
                    if (resp != null)
                    {
                        //a.log.Debugf("[%s] RESET RESPONSE: %+v", a.name, resp)
                        reply.Add(resp);
                    }
                }
            }

            var hasPacketLoss = (payloadQueue.size() > 0);
            if (hasPacketLoss)
            {
                logger.LogTrace($"{name} packetloss: %s");//, a.payloadQueue.getGapAckBlocksString(a.peerLastTSN))
            }

            if ((ackState != AcknowlegeState.Immediate && !sackImmediately && !hasPacketLoss && ackMode == AckMode.Normal) || ackMode == AckMode.AlwaysDelay)
            {
                if (ackState == AcknowlegeState.Idle)
                {
                    delayedAckTriggered = true;
                }
                else
                {
                    immediateAckTriggered = true;
                }
            }
            else
            {
                immediateAckTriggered = true;
            }

            return reply.ToArray();
        }

        void unregisterStream(SCTPStream s) 
        {
            lock (myLock)
            {
                _streams.TryRemove(s.getNum(), out var st);
                s.close();
            }
        }


        // The caller should hold the lock.
        Packet resetStreamsIfAny(OutgoingSSNResetRequestParameter p)
        {
            var result = ReconfigResult.SuccessPerformed;

            if (Utils.sna32LTE(p.getLastAssignedTSN(), peerLastTSN))
            {
                logger.LogDebug($"{name} resetStream(): senderLastTSN={p.getLastAssignedTSN()} <= peerLastTSN={peerLastTSN}");
                foreach (var id in p.getStreams())
                {
                    if (!_streams.TryGetValue(id, out var s))
                    {
                        continue;
                    }

                    unregisterStream(s);
                }
                reconfigRequests.Remove(p.getReqSeqNo());
            }
            else
            {
                logger.LogDebug($"{name} resetStream(): senderLastTSN={p.getLastAssignedTSN()} > peerLastTSN={peerLastTSN}");
                result = ReconfigResult.ResultInProgress;
            }
            ReConfigChunk reply = new ReConfigChunk();
            var rep = new ReconfigurationResponseParameter(p.getReqSeqNo());
            rep.setResult((uint)result);
            reply.addParam(rep);
            return makePacket(reply);
        }


        // todo should be in a behave block
        // then we wouldn't be messing with stream seq numbers.

        private Chunk[] dcepDeal(SCTPStream s, DataChunk dc, DataChannelOpen dcep)
        {
            Chunk[] rep = null;
            //logger.LogDebug("dealing with a decp for stream " + dc.getDataAsString());
            if (!dcep.isAck())
            {
                //logger.LogDebug("decp is not an ack... ");

                SCTPStreamBehaviour behave = dcep.mkStreamBehaviour();
                s.setBehave(behave);
                s.setLabel(dcep.getLabel());
                lock (s)
                {
                    int seqIn = s.getNextMessageSeqIn();
                    s.setNextMessageSeqIn(seqIn + 1);
                    int seqOut = s.getNextMessageSeqOut();
                    s.setNextMessageSeqOut(seqOut + 1);
                }
                rep = new Chunk[1];
                DataChunk ack = dc.mkAck(dcep);
                s.outbound(ack);
                ack.setTsn(myNextTSN++);
                // check rollover - will break at maxint.
                rep[0] = ack;

            }
            else
            {
                //logger.LogDebug("got a dcep ack for " + s.getLabel());
                SCTPStreamBehaviour behave = dcep.mkStreamBehaviour();
                s.setBehave(behave);
                lock (s)
                {
                    int seqIn = s.getNextMessageSeqIn();
                    s.setNextMessageSeqIn(seqIn + 1);
                    int seqOut = s.getNextMessageSeqOut();
                    s.setNextMessageSeqOut(seqOut + 1);
                }
            }
            return rep;
        }

        /**
		 * <code>
		 * 2)  Authenticate the State Cookie as one that it previously generated
		 * by comparing the computed MAC against the one carried in the
		 * State Cookie.  If this comparison fails, the SCTP packet,
		 * including the COOKIE ECHO and any DATA chunks, should be silently
		 * discarded,
		 *
		 * 3)  Compare the port numbers and the Verification Tag contained
		 * within the COOKIE ECHO chunk to the actual port numbers and the
		 * Verification Tag within the SCTP common header of the received
		 * packet.  If these values do not match, the packet MUST be
		 * silently discarded.
		 *
		 * 4)  Compare the creation timestamp in the State Cookie to the current
		 * local time.  If the elapsed time is longer than the lifespan
		 * carried in the State Cookie, then the packet, including the
		 * COOKIE ECHO and any attached DATA chunks, SHOULD be discarded,
		 * and the endpoint MUST transmit an ERROR chunk with a "Stale
		 * Cookie" error cause to the peer endpoint.
		 *
		 * 5)  If the State Cookie is valid, create an association to the sender
		 * of the COOKIE ECHO chunk with the information in the TCB data
		 * carried in the COOKIE ECHO and enter the ESTABLISHED state.
		 *
		 * 6)  Send a COOKIE ACK chunk to the peer acknowledging receipt of the
		 * COOKIE ECHO.  The COOKIE ACK MAY be bundled with an outbound DATA
		 * chunk or SACK chunk; however, the COOKIE ACK MUST be the first
		 * chunk in the SCTP packet.
		 *
		 * 7)  Immediately acknowledge any DATA chunk bundled with the COOKIE
		 * ECHO with a SACK (subsequent DATA chunk acknowledgement should
		 * follow the rules defined in Section 6.2).  As mentioned in step
		 * 6, if the SACK is bundled with the COOKIE ACK, the COOKIE ACK
		 * MUST appear first in the SCTP packet.
		 * </code>
		 */
        private Chunk[] cookieEchoDeal(CookieEchoChunk echo)
        {
            Chunk[] reply = new Chunk[0];
            if (_state == State.CLOSED || _state == State.COOKIEWAIT || _state == State.COOKIEECHOED)
            {
                // Authenticate the State Cookie
                CookieHolder cookie;
                if (null != (cookie = checkCookieEcho(echo.getCookieData())))
                {
                    t1Init.stop();
                    t1Cookie.stop();
                    // Compare the creation timestamp in the State Cookie to the current local time.
                    uint howStale = howStaleIsMyCookie(cookie);
                    if (howStale == 0)
                    {
                        //enter the ESTABLISHED state
                        _state = State.ESTABLISHED;
                        /*
						 Send a COOKIE ACK chunk to the peer acknowledging receipt of the
						 COOKIE ECHO.  The COOKIE ACK MAY be bundled with an outbound DATA
						 chunk or SACK chunk; however, the COOKIE ACK MUST be the first
						 chunk in the SCTP packet.
						 */
                        reply = new Chunk[1];
                        reply[0] = new CookieAckChunk();
                    }
                    else
                    {
                        reply = new Chunk[1];
                        /* If the elapsed time is longer than the lifespan
						 * carried in the State Cookie, then the packet, including the
						 * COOKIE ECHO and any attached DATA chunks, SHOULD be discarded,
						 * and the endpoint MUST transmit an ERROR chunk with a "Stale
						 * Cookie" error cause to the peer endpoint.*/
                        StaleCookieError sce = new StaleCookieError();
                        sce.setMeasure(howStale * 1000);
                        ErrorChunk ec = new ErrorChunk(sce);
                        reply[0] = ec;
                    }
                }
                else
                {
                    logger.LogError("Got a COOKIE_ECHO that doesn't match any we sent. ?!?");
                }
            }
            else
            {
                logger.LogDebug("Got an COOKIE_ECHO when not closed - ignoring it");
            }
            return reply;
        }

        uint generateNextTSN()
        {
            var tsn = myNextTSN;
            myNextTSN++;
            return tsn;
        }

        private SackChunk mkSack()
        {
            SackChunk ret = new SackChunk();
            ret.setCumuTSNAck(peerLastTSN);
            int stashcap = calcStashCap();
            ret.advertisedReceiverWindowCredit = (uint)(MAXBUFF - stashcap);
            ret.gapAckBlocks = pendingQueue.getGapAckBlocks(peerLastTSN);
            ret.duplicateTSN = pendingQueue.popDuplicates();
            //logger.LogDebug("made SACK " + ret.ToString());
            return ret;
        }

        private int calcStashCap()
        {
            int ret = 0;
            foreach (SCTPStream s in this._streams.Values)
            {
                ret += s.stashCap();
            }
            return ret;
        }

        public abstract void enqueue(DataChunk d);

        public abstract SCTPStream mkStream(int id);


        public uint getCumAckPt()
        {
            return peerLastTSN;
        }
        public ReConfigChunk addToCloseList(SCTPStream st)
        {
            return reconfigState.makeClose(st);
        }

        public void closeStream(SCTPStream st)
        {
            Chunk[] cs = new Chunk[1];
            if (canSend())
            {
                //logger.LogDebug("due to reconfig stream " + st);
                cs[0] = reconfigState.makeClose(st);

                this.controlQueue.push(makePacket(cs[0]));
            }
        }

        public SCTPStream mkStream(string label)
        {
            //int n = 1;
            //int tries = this._maxOutStreams;
            //do
            //{
            //    n = 2 * _random.Next(this._maxOutStreams);
            //    if (!_even) n += 1;
            //    if (--tries < 0)
            //    {
            //        logger.LogError("StreamNumberInUseException");
            //        return null;
            //    }
            //} while (_streams.ContainsKey(n));
            int n = _nextStreamID;
            _nextStreamID += 2;
            return mkStream(n, label);
        }

        public int[] allStreams()
        {
            var ks = _streams.Keys;
            int[] ret = new int[ks.Count];
            int i = 0;
            foreach (int k in ks)
            {
                ret[i++] = k;
            }
            return ret;
        }
        public SCTPStream getStream(int s)
        {
            SCTPStream stream;
            return _streams.TryGetValue(s, out stream) ? stream : null;
        }

        public SCTPStream delStream(int s)
        {
            if (!_streams.ContainsKey(s))
            {
                return null;
            }
            var st = _streams[s];
            _streams.TryRemove(s, out st);
            return st;
        }

        public SCTPStream mkStream(int sno, string label)
        {
            SCTPStream sout;
            if (canSend())
            {
                lock (_streams)
                {
                    if (_streams.ContainsKey(sno))
                    {
                        logger.LogError("StreamNumberInUseException");
                        return null;
                    }
                    sout = mkStream(sno);
                    sout.setLabel(label);
                    _streams.TryAdd(sno, sout);
                }// todo - move this to behave
                DataChunk DataChannelOpen = DataChunk.mkDataChannelOpen(label);
                sout.outbound(DataChannelOpen);
                DataChannelOpen.setTsn(myNextTSN++);
                logger.LogDebug($"SCTP data channel open chunk {DataChannelOpen}.");
                try
                {
                    var pkt = makePacket(DataChannelOpen);
                    controlQueue.push(pkt);
                }
                catch (Exception end)
                {
                    unexpectedClose(end);
                    logger.LogError(end.ToString());
                }
            }
            else
            {
                throw new UnreadyAssociationException();
            }
            return sout;
        }

        //public int maxMessageSize()
        //{
        //    return 1 << 20; // shrug - I don't know 
        //}

        public bool canSend()
        {
            bool ok;
            switch (_state)
            {
                case State.ESTABLISHED:
                case State.SHUTDOWNPENDING:
                case State.SHUTDOWNRECEIVED:
                    ok = true;
                    break;
                default:
                    ok = false;
                    break;
            }
            return ok;
        }

        protected void unexpectedClose(Exception end)
        {
            _rcv = null;
            _send = null;
            _al.onDisAssociated(this);
            _state = State.CLOSED;
            closeAllTimers();
        }
        void closeAllTimers()
        {
            // Close all retransmission & ack timers
            t1Init.close();
            t1Cookie.close();
            t3RTX.close();
            tReconfig.close();
            _ackTimer.close();
        }

        abstract internal void sendAndBlock(SCTPMessage m);

        abstract internal SCTPMessage makeMessage(byte[] bytes, BlockingSCTPStream aThis);

        abstract internal SCTPMessage makeMessage(string s, BlockingSCTPStream aThis);

        protected void handleSack(SackChunk d)
        {
            logger.LogTrace($"{name} SACK: cumTSN={d.cumulativeTSNAck} _rwnd={d.getArWin()}");
            var state = this._state;
            if (state != State.ESTABLISHED)
            {
                return;
            }
            stats.incSACKs();

            if (Utils.sna32GT(cumulativeTSNAckPoint, d.cumulativeTSNAck))
            {
                // RFC 4960 sec 6.2.1.  Processing a Received SACK
                // D)
                //   i) If Cumulative TSN Ack is less than the Cumulative TSN Ack
                //      Point, then drop the SACK.  Since Cumulative TSN Ack is
                //      monotonically increasing, a SACK whose Cumulative TSN Ack is
                //      less than the Cumulative TSN Ack Point indicates an out-of-
                //      order SACK.

                logger.LogDebug($"{name} SACK Cumulative ACK {d.cumulativeTSNAck} is older than ACK point {cumulativeTSNAckPoint}");
                return;
            }

            var bytesAckedPerStream = processSelectiveAck(d, out var htna);

            uint totalBytesAcked = 0;
            foreach (var nBytesAcked in bytesAckedPerStream)
            {
                totalBytesAcked += nBytesAcked.Value;
            }
            var cumTSNAckPointAdvanced = false;

            if (Utils.sna32LT(cumulativeTSNAckPoint, d.cumulativeTSNAck))
            {
                logger.LogTrace($"{name} SACK: cumTSN advanced: {cumulativeTSNAckPoint} -> {d.cumulativeTSNAck}");

                cumulativeTSNAckPoint = d.cumulativeTSNAck;

                cumTSNAckPointAdvanced = true;

                onCumulativeTSNAckPointAdvanced(totalBytesAcked);
            }

            foreach (var item in bytesAckedPerStream)
            {
                var si = item.Key;
                var nBytesAcked = item.Value;
                if (_streams.TryGetValue(si, out var s))
                {
                    s.onBufferReleased(nBytesAcked);
                }
            }

            // New rwnd value
            // RFC 4960 sec 6.2.1.  Processing a Received SACK
            // D)
            //   ii) Set rwnd equal to the newly received a_rwnd minus the number
            //       of bytes still outstanding after processing the Cumulative
            //       TSN Ack and the Gap Ack Blocks.

            // bytes acked were already subtracted by markAsAcked() method
            var bytesOutstanding = inflightQueue.getNumBytes();
            if (bytesOutstanding >= d.getArWin())
            {
                _rwnd = 0;
            }
            else
            {
                _rwnd = (uint)d.getArWin() - bytesOutstanding;
            }

            processFastRetransmission(d.cumulativeTSNAck, htna, cumTSNAckPointAdvanced);

            if (useForwardTSN)
            {
                // RFC 3758 Sec 3.5 C1
                if (Utils.sna32LT(advancedPeerTSNAckPoint, cumulativeTSNAckPoint))
                {
                    advancedPeerTSNAckPoint = cumulativeTSNAckPoint;
                }

                // RFC 3758 Sec 3.5 C2
                for (var i = advancedPeerTSNAckPoint + 1; ; i++)
                {
                    if (!inflightQueue.get(i, out var c))
                    {
                        break;
                    }

                    if (!c.abandoned())
                    {
                        break;
                    }
                    advancedPeerTSNAckPoint = i;
                }

                // RFC 3758 Sec 3.5 C3
                if (Utils.sna32GT(advancedPeerTSNAckPoint, cumulativeTSNAckPoint))
                {
                    willSendForwardTSN = true;
                }
            }

            if (inflightQueue.size() > 0)
            {
                // Start timer. (noop if already started)
                logger.LogTrace($"{name} T3-rtx timer start (pt3)");

                t3RTX.start(rtoMgr.getRTO());
            }

            if (cumTSNAckPointAdvanced)
            {
                //    a.awakeWriteLoop()
            }
        }

        void processFastRetransmission(uint cumTSNAckPoint, uint htna, bool cumTSNAckPointAdvanced)
        {
            // HTNA algorithm - RFC 4960 Sec 7.2.4
            // Increment missIndicator of each chunks that the SACK reported missing
            // when either of the following is met:
            // a)  Not in fast-recovery
            //     miss indications are incremented only for missing TSNs prior to the
            //     highest TSN newly acknowledged in the SACK.
            // b)  In fast-recovery AND the Cumulative TSN Ack Point advanced
            //     the miss indications are incremented for all TSNs reported missing
            //     in the SACK.
            if (!inFastRecovery || (inFastRecovery && cumTSNAckPointAdvanced))
            {
                uint maxTSN;
                if (!inFastRecovery)
                {
                    // a) increment only for missing TSNs prior to the HTNA
                    maxTSN = htna;
                }
                else
                {
                    // b) increment for all TSNs reported missing
                    maxTSN = cumTSNAckPoint + (uint)inflightQueue.size() + 1;
                }

                for (uint tsn = cumTSNAckPoint + 1; Utils.sna32LT(tsn, maxTSN); tsn++)
                {
                    if (!inflightQueue.get(tsn, out var c))
                    {
                        throw new Exception("errTSNRequestNotExist " + tsn);
                    }
                    if (!c.acked && !c.abandoned() && c.missIndicator < 3)
                    {
                        c.missIndicator++;
                        if (c.missIndicator == 3)
                        {
                            if (!inFastRecovery)
                            {
                                // 2)  If not in Fast Recovery, adjust the ssthresh and cwnd of the
                                //     destination address(es) to which the missing DATA chunks were
                                //     last sent, according to the formula described in Section 7.2.3.
                                inFastRecovery = true;

                                fastRecoverExitPoint = htna;

                                _ssthresh = max32(_cwnd / 2, 4 * mtu);

                                _cwnd = _ssthresh;
                                partialBytesAcked = 0;
                                willRetransmitFast = true;

                                logger.LogTrace($"{name} updated cwnd={_cwnd} ssthresh={_ssthresh} inflight={inflightQueue.getNumBytes()}(FR)");
                            }
                        }
                    }
                }
            }

            if (inFastRecovery && cumTSNAckPointAdvanced)
            {
                willRetransmitFast = true;
            }
        }

        // The caller should hold the lock.
        void onCumulativeTSNAckPointAdvanced(uint totalBytesAcked)
        {
            // RFC 4096, sec 6.3.2.  Retransmission Timer Rules
            //   R2)  Whenever all outstanding data sent to an address have been
            //        acknowledged, turn off the T3-rtx timer of that address.
            if (inflightQueue.size() == 0)
            {
                logger.LogTrace($"{name} SACK: no more packet in-flight (pending={pendingQueue.size()})");
                t3RTX.stop();
            }
            else
            {
                logger.LogTrace($"{name} T3-rtx timer start (pt2)");
                t3RTX.start(rtoMgr.getRTO());
            }

            // Update congestion control parameters
            if (_cwnd <= _ssthresh)
            {
                // RFC 4096, sec 7.2.1.  Slow-Start
                //   o  When cwnd is less than or equal to ssthresh, an SCTP endpoint MUST
                //		use the slow-start algorithm to increase cwnd only if the current
                //      congestion window is being fully utilized, an incoming SACK
                //      advances the Cumulative TSN Ack Point, and the data sender is not
                //      in Fast Recovery.  Only when these three conditions are met can
                //      the cwnd be increased; otherwise, the cwnd MUST not be increased.
                //		If these conditions are met, then cwnd MUST be increased by, at
                //      most, the lesser of 1) the total size of the previously
                //      outstanding DATA chunk(s) acknowledged, and 2) the destination's
                //      path MTU.
                if (!inFastRecovery &&
                    pendingQueue.size() > 0)
                {
                    _cwnd += min32(totalBytesAcked, _cwnd); // TCP way
                                                            // a.cwnd += min32(uint32(totalBytesAcked), a.mtu) // SCTP way (slow)
                    logger.LogTrace($"{name} updated cwnd={_cwnd} ssthresh={_ssthresh} acked={totalBytesAcked} (SS)");
                }
                else
                {
                    logger.LogInformation($"{name} cwnd did not grow: cwnd={_cwnd} ssthresh={_ssthresh} acked={totalBytesAcked} FR={inFastRecovery} pending={pendingQueue.size()}");
                }
            }
            else
            {
                // RFC 4096, sec 7.2.2.  Congestion Avoidance
                //   o  Whenever cwnd is greater than ssthresh, upon each SACK arrival
                //      that advances the Cumulative TSN Ack Point, increase
                //      partial_bytes_acked by the total number of bytes of all new chunks
                //      acknowledged in that SACK including chunks acknowledged by the new
                //      Cumulative TSN Ack and by Gap Ack Blocks.
                partialBytesAcked += totalBytesAcked;

                //   o  When partial_bytes_acked is equal to or greater than cwnd and
                //      before the arrival of the SACK the sender had cwnd or more bytes
                //      of data outstanding (i.e., before arrival of the SACK, flight size
                //      was greater than or equal to cwnd), increase cwnd by MTU, and
                //      reset partial_bytes_acked to (partial_bytes_acked - cwnd).
                if (partialBytesAcked >= _cwnd && pendingQueue.size() > 0)
                {
                    partialBytesAcked -= _cwnd;
                    _cwnd += mtu;
                    logger.LogTrace($"{name} updated cwnd={_cwnd} ssthresh={_ssthresh} acked={totalBytesAcked} (CA)");
                }
            }
        }

        Dictionary<int, uint> processSelectiveAck(SackChunk d, out uint htna)
        {
            var bytesAckedPerStream = new Dictionary<int, uint>();
            htna = 0;
            // New ack point, so pop all ACKed packets from inflightQueue
            // We add 1 because the "currentAckPoint" has already been popped from the inflight queue
            // For the first SACK we take care of this by setting the ackpoint to cumAck - 1
            if (inflightQueue.getOldestTSNReceived(out var oldTsn))
            {
                if (oldTsn - 1 > cumulativeTSNAckPoint)
                {
                    cumulativeTSNAckPoint = oldTsn - 1;
                }
            }
            for (var i = cumulativeTSNAckPoint + 1; Utils.sna32LTE(i, d.cumulativeTSNAck); i++)
            {
                if (!inflightQueue.pop(i, out var c))
                {
                    return bytesAckedPerStream;
                }

                if (!c.acked)
                {
                    // RFC 4096 sec 6.3.2.  Retransmission Timer Rules
                    //   R3)  Whenever a SACK is received that acknowledges the DATA chunk
                    //        with the earliest outstanding TSN for that address, restart the
                    //        T3-rtx timer for that address with its current RTO (if there is
                    //        still outstanding data on that address).
                    if (i == cumulativeTSNAckPoint + 1)
                    {
                        // T3 timer needs to be reset. Stop it for now.
                        t3RTX.stop();
                    }

                    var nBytesAcked = inflightQueue.markAsAcked(c);

                    if (bytesAckedPerStream.TryGetValue(c.getStreamId(), out var amount))
                    {
                        bytesAckedPerStream[c.getStreamId()] = amount + nBytesAcked;
                    }
                    else
                    {
                        bytesAckedPerStream.Add(c.getStreamId(), nBytesAcked);
                    }

                    // RFC 4960 sec 6.3.1.  RTO Calculation
                    //   C4)  When data is in flight and when allowed by rule C5 below, a new
                    //        RTT measurement MUST be made each round trip.  Furthermore, new
                    //        RTT measurements SHOULD be made no more than once per round trip
                    //        for a given destination transport address.
                    //   C5)  Karn's algorithm: RTT measurements MUST NOT be made using
                    //        packets that were retransmitted (and thus for which it is
                    //        ambiguous whether the reply was for the first instance of the
                    //        chunk or for a later instance)
                    if (c.nSent == 1 && Utils.sna32GTE(c.tsn, minTSN2MeasureRTT))
                    {
                        minTSN2MeasureRTT = myNextTSN;

                        var time = TimeExtension.CurrentTimeMillis();
                        var rtt = time - c.getSentTime();

                        var srtt = rtoMgr.setNewRTT(rtt);

                        logger.LogTrace($"{name} SACK: measured-rtt={rtt} srtt={srtt} new-rto={rtoMgr.getRTO()}");

                    }
                }

                if (inFastRecovery && c.tsn == fastRecoverExitPoint)
                {
                    logger.LogDebug($"{name} exit fast-recovery");

                    inFastRecovery = false;
                }
            }

            htna = d.cumulativeTSNAck;

            // Mark selectively acknowledged chunks as "acked"
            foreach (var g in d.gapAckBlocks)
            {
                for (var i = g.start; i <= g.end; i++)
                {
                    var tsn = d.cumulativeTSNAck + i;
                    if (!inflightQueue.get(tsn, out var c))
                    {
                        logger.LogDebug($"{name}errTSNRequestNotExist: {tsn}");
                        return bytesAckedPerStream;// nil, 0, fmt.Errorf("%w: %v", errTSNRequestNotExist, tsn)
                    }

                    if (!c.acked)
                    {
                        var nBytesAcked = inflightQueue.markAsAcked(tsn);

                        if (bytesAckedPerStream.TryGetValue(c.getStreamId(), out var amount))
                        {
                            bytesAckedPerStream[c.getStreamId()] = amount + nBytesAcked;
                        }
                        else
                        {
                            bytesAckedPerStream.Add(c.getStreamId(), nBytesAcked);
                        }

                        logger.LogTrace($"{name} tsn={c.tsn} has been sacked");

                        if (c.nSent == 1)
                        {
                            minTSN2MeasureRTT = myNextTSN;

                            var time = TimeExtension.CurrentTimeMillis();
                            var rtt = time - c.getSentTime();
                            var srtt = rtoMgr.setNewRTT(rtt);

                            logger.LogTrace($"{name} SACK: measured-rtt={rtt} srtt={srtt} new-rto={rtoMgr.getRTO()}");
                        }

                        if (Utils.sna32LT(htna, tsn))
                        {
                            htna = tsn;
                        }
                    }
                }
            }
            return bytesAckedPerStream;
        }

        public void onRetransmissionTimeout(int id, uint nRtos)
        {
            try
            {
                this.RWMutex.WaitOne();

                if (id == (int)TimerType.T1Init)
                {
                    sendInit();
                    return;
                }

                if (id == (int)TimerType.T1Cookie)
                {
                    sendCookieEcho();
                    return;
                }

                if (id == (int)TimerType.T3RTX)
                {
                    stats.incT3Timeouts();

                    // RFC 4960 sec 6.3.3
                    //  E1)  For the destination address for which the timer expires, adjust
                    //       its ssthresh with rules defined in Section 7.2.3 and set the
                    //       cwnd <- MTU.
                    // RFC 4960 sec 7.2.3
                    //   When the T3-rtx timer expires on an address, SCTP should perform slow
                    //   start by:
                    //      ssthresh = max(cwnd/2, 4*MTU)
                    //      cwnd = 1*MTU

                    _ssthresh = max32(_cwnd / 2, 4 * mtu);
                    _cwnd = mtu;
                    logger.LogTrace($"{name} updated cwnd={_cwnd} ssthresh={_ssthresh} inflight={inflightQueue.getNumBytes()}(RTO)");

                    // RFC 3758 sec 3.5
                    //  A5) Any time the T3-rtx timer expires, on any destination, the sender
                    //  SHOULD try to advance the "Advanced.Peer.Ack.Point" by following
                    //  the procedures outlined in C2 - C5.
                    if (useForwardTSN)
                    {
                        // RFC 3758 Sec 3.5 C2
                        for (var i = advancedPeerTSNAckPoint + 1; ; i++)
                        {
                            if (!inflightQueue.get(i, out var c))
                            {
                                break;
                            }
                            if (!c.abandoned())
                            {
                                break;
                            }
                            advancedPeerTSNAckPoint = i;
                        }

                        // RFC 3758 Sec 3.5 C3
                        if (Utils.sna32GT(advancedPeerTSNAckPoint, cumulativeTSNAckPoint))
                        {
                            willSendForwardTSN = true;
                        }
                    }

                    //a.log.Debugf("[%s] T3-rtx timed out: nRtos=%d cwnd=%d ssthresh=%d", a.name, nRtos, a.cwnd, a.ssthresh)

                    /*
                        a.log.Debugf("   - advancedPeerTSNAckPoint=%d", a.advancedPeerTSNAckPoint)
                        a.log.Debugf("   - cumulativeTSNAckPoint=%d", a.cumulativeTSNAckPoint)
                        a.inflightQueue.updateSortedKeys()
                        for i, tsn := range a.inflightQueue.sorted {
                            if c, ok := a.inflightQueue.get(tsn); ok {
                                a.log.Debugf("   - [%d] tsn=%d acked=%v abandoned=%v (%v,%v) len=%d",
                                    i, c.tsn, c.acked, c.abandoned(), c.beginningFragment, c.endingFragment, len(c.userData))
                            }
                        }
                    */

                    inflightQueue.markAllToRetrasmit();
                    //a.awakeWriteLoop()

                    return;
                }

                if (id == (int)TimerType.Reconfig)
                {
                    willRetransmitReconfig = true;
                    //a.awakeWriteLoop()
                }
            }
            finally
            {
                RWMutex.ReleaseMutex();
            }
        }

        public void onRetransmissionFailure(int id)
        {
            try
            {
                RWMutex.WaitOne();

                if (id == (int)TimerType.T1Init)
                {
                    logger.LogError($"{name} retransmission failure: T1-init");
                    //a.handshakeCompletedCh <- errHandshakeInitAck;

                    return;
                }

                if (id == (int)TimerType.T1Cookie)
                {
                    logger.LogError($"{name} retransmission failure: T1-cookie");
                    //a.handshakeCompletedCh <- errHandshakeCookieEcho

                    return;
                }

                if (id == (int)TimerType.T3RTX)
                {
                    // T3-rtx timer will not fail by design
                    // Justifications:
                    //  * ICE would fail if the connectivity is lost
                    //  * WebRTC spec is not clear how this incident should be reported to ULP
                    logger.LogError($"{name} retransmission failure: T3-rtx (DATA)");

                    return;
                }
            }
            finally
            {
                RWMutex.ReleaseMutex();
            }
        }

        public void onAckTimeout()
        {
            lock (myLock)
            {
                logger.LogTrace($"{name} ack timed out (ackState: {ackState})");
                stats.incAckTimeouts();
                ackState = AcknowlegeState.Immediate;
            }
        }

        // bufferedAmount returns total amount (in bytes) of currently buffered user data.
        // This is used only by testing.
        uint bufferedAmount()
        {
            lock (myLock)
            {

                return pendingQueue.getNumBytes() + inflightQueue.getNumBytes();
            }
        }

        // MaxMessageSize returns the maximum message size you can send.
        uint MaxMessageSize()
        {
            lock (myLock)
            {
                return maxMessageSize;
            }
        }


        // SetMaxMessageSize sets the maximum message size you can send.
        void SetMaxMessageSize(uint maxMsgSize)
        {
            lock (myLock)
            {
                maxMessageSize = maxMsgSize;
            }
        }
    }
}
