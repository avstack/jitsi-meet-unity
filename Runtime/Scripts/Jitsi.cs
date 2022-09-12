using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Unity.WebRTC;
using UnityEngine;
using UnityEngine.UI;

namespace AVStack.Jitsi
{
  public static class Jitsi
  {
    internal const string Lib = "jitsi_meet_signalling_c";

    internal static Context context;
    internal static ConcurrentQueue<IEnumerator> asyncOperationQueue;

    public static void Initialize()
    {
      if (context != null)
        throw new InvalidOperationException("Already initialized Jitsi.");

      Debug.Log("Initialising WebRTC");
      WebRTC.Initialize();

      Debug.Log("Initialising Jitsi logging");
      if (!NativeMethods.jitsi_logging_init_file("/Users/jbg/dev/avstack/jitsi-unity.log", "debug")) {
        Debug.Log("Failed to initialise Jitsi logging");
      }

      Debug.Log("Initialising Jitsi native");
      context = new Context();

      asyncOperationQueue = new ConcurrentQueue<IEnumerator>();
    }

    public static void Dispose()
    {
      WebRTC.Dispose();
    }

    public static IEnumerator Update(MonoBehaviour parent)
    {
      Debug.Log("Starting WebRTC background task");
      parent.StartCoroutine(WebRTC.Update());

      IEnumerator op;
      while (true)
      {
        if (asyncOperationQueue.TryDequeue(out op))
        {
          Debug.Log("Running Jitsi background work");
          yield return parent.StartCoroutine(op);
        }
        yield return null;
      }
    }
  }

  public class Context
  {
    internal IntPtr nativeContext;

    internal Context()
    {
      nativeContext = NativeMethods.jitsi_context_create();
    }

    ~Context()
    {
      NativeMethods.jitsi_context_free(nativeContext);
    }
  }

  public class Connection
  {
    internal IntPtr nativeConnection;

    public Connection(string websocketUrl, string xmppDomain, bool tlsInsecure)
    {
      nativeConnection = NativeMethods.jitsi_connection_connect(Jitsi.context.nativeContext, websocketUrl, xmppDomain, tlsInsecure);
    }

    ~Connection()
    {
      NativeMethods.jitsi_connection_free(nativeConnection);
    }

    public Conference join(string conferenceName, string nick, MediaStreamTrack[] localTracks, IConferenceDelegate conferenceDelegate)
    {
      return new Conference(this, conferenceName, nick, localTracks, conferenceDelegate);
    }
  }

  public class Participant
  {
    public string jid;
    public string nick;

    internal Participant(IntPtr nativeParticipant)
    {
      var jid = NativeMethods.jitsi_participant_jid(nativeParticipant);
      this.jid = jid.AsString();
      var nick = NativeMethods.jitsi_participant_nick(nativeParticipant);
      this.nick = nick.AsString();
    }
  }

  public interface IConferenceDelegate
  {
    IEnumerator ParticipantJoined(Participant participant);
    IEnumerator ParticipantLeft(Participant participant);
    IEnumerator RemoteAudioTrackAdded(AudioStreamTrack audioTrack);
    IEnumerator RemoteAudioTrackRemoved(AudioStreamTrack audioTrack);
    IEnumerator RemoteVideoTrackAdded(VideoStreamTrack videoTrack);
    IEnumerator RemoteVideoTrackRemoved(VideoStreamTrack videoTrack);
    IEnumerator VideoReceived(string trackId, Texture texture);
    IEnumerator SessionTerminate();
  }

  public class Conference
  {
    private MediaStreamTrack[] localTracks;

    private RTCPeerConnection peerConnection;
    private MediaStream mediaStream;

    private Connection connection;
    private IConferenceDelegate conferenceDelegate;
    private IntPtr nativeConference;

    internal Conference(Connection connection, string conferenceName, string nick, MediaStreamTrack[] localTracks, IConferenceDelegate conferenceDelegate)
    {
      this.conferenceDelegate = conferenceDelegate;

      Debug.Log("Creating RTCPeerConnection");
      this.localTracks = localTracks;
      this.InitPeerConnection();

      this.connection = connection;
      var agent = new Agent {
        opaque = GCHandle.ToIntPtr(GCHandle.Alloc(this)),
        participant_joined = ParticipantJoined,
        participant_left = ParticipantLeft,
        colibri_message_received = ColibriMessageReceived,
        offer_received = OfferReceived,
      };
      Debug.Log($"Created Agent opaque={agent.opaque}");
      this.nativeConference = NativeMethods.jitsi_connection_join(Jitsi.context.nativeContext, connection.nativeConnection, conferenceName, nick, ref agent);
    }

    ~Conference()
    {
      // Leave
    }

    public string LocalEndpointId()
    {
      return NativeMethods.jitsi_conference_local_endpoint_id(this.nativeConference).AsString();
    }

    private void InitPeerConnection()
    {
      this.peerConnection = new RTCPeerConnection();
      this.mediaStream = new MediaStream();

      this.peerConnection.OnTrack = e =>
      {
        var mediaStream = e.Streams.First();

        if (e.Track is VideoStreamTrack videoTrack)
        {
          Debug.Log($"Video track added: {videoTrack}");
          videoTrack.OnVideoReceived += texture =>
          {
            Debug.Log($"Received new video texture: {texture} for track: {videoTrack}");
            Jitsi.asyncOperationQueue.Enqueue(this.conferenceDelegate.VideoReceived(videoTrack.Id, texture));
          };
          Jitsi.asyncOperationQueue.Enqueue(this.conferenceDelegate.RemoteVideoTrackAdded(videoTrack));

          mediaStream.OnRemoveTrack = e =>
          {
            Debug.Log($"Video track removed: {videoTrack}");
            Jitsi.asyncOperationQueue.Enqueue(this.conferenceDelegate.RemoteVideoTrackRemoved(videoTrack));
          };
        }
        else if (e.Track is AudioStreamTrack audioTrack)
        {
          Debug.Log($"Audio track added: {audioTrack}");
          Jitsi.asyncOperationQueue.Enqueue(this.conferenceDelegate.RemoteAudioTrackAdded(audioTrack));
          
          mediaStream.OnRemoveTrack = e =>
          {
            Debug.Log($"Audio track removed: {audioTrack}");
            Jitsi.asyncOperationQueue.Enqueue(this.conferenceDelegate.RemoteAudioTrackRemoved(audioTrack));
          };
        }
      };

      this.peerConnection.OnNegotiationNeeded = () =>
      {
        Debug.Log("Negotiation needed");
      };

      this.peerConnection.OnConnectionStateChange = (state) =>
      {
        Debug.Log($"Connection state change: {state}");
      };

      this.peerConnection.OnIceCandidate = (candidate) =>
      {
        Debug.Log($"ICE candidate: {candidate}");
      };

      this.peerConnection.OnIceGatheringStateChange = (state) =>
      {
        Debug.Log($"ICE gathering state change: {state}");
      };

      this.peerConnection.OnIceConnectionChange = (state) =>
      {
        Debug.Log($"ICE connection change: {state}");
      };

      foreach (var track in this.localTracks)
      {
        Debug.Log($"Adding local track: {track}");
        this.mediaStream.AddTrack(track);
        this.peerConnection.AddTrack(track, this.mediaStream);
      }
    }

    private IEnumerator Negotiation(string remoteOffer, bool shouldSendAnswer)
    {
      Debug.Log("Got remote offer");
      var remoteDescription = new RTCSessionDescription {
        sdp = remoteOffer,
        type = RTCSdpType.Offer,
      };

      Debug.Log($"Setting remote description on PC {this.peerConnection}:\n{remoteOffer}");
      var setRemoteDescriptionOp = this.peerConnection.SetRemoteDescription(ref remoteDescription);
      yield return setRemoteDescriptionOp;

      Debug.Log("Creating local answer");
      var answerOp = this.peerConnection.CreateAnswer();
      yield return answerOp;
      var localDescription = answerOp.Desc;

      Debug.Log($"Setting local description on PC {this.peerConnection}:\n{localDescription.sdp}");
      var setLocalDescriptionOp = this.peerConnection.SetLocalDescription(ref localDescription);
      yield return setLocalDescriptionOp;
      
      if (shouldSendAnswer)
      {
        Debug.Log("Sending accept");
        NativeMethods.jitsi_conference_accept(Jitsi.context.nativeContext, this.nativeConference, localDescription.sdp);
      }
    }

    private void SessionTerminateInternal()
    {
      this.peerConnection.Close();
      Jitsi.asyncOperationQueue.Enqueue(this.conferenceDelegate.SessionTerminate());
      this.InitPeerConnection();
    }

    internal static void SessionTerminate(IntPtr opaque)
    {
      Debug.Log($"Terminating session opaque={opaque}");
      var conference = GCHandle.FromIntPtr(opaque).Target as Conference;
      conference.SessionTerminateInternal();
    }

    internal static void ParticipantJoined(IntPtr opaque, IntPtr nativeParticipant)
    {
      Debug.Log($"Jitsi callback: participant_joined opaque={opaque}");
      var conference = GCHandle.FromIntPtr(opaque).Target as Conference;
      var participant = new Participant(nativeParticipant);
      Jitsi.asyncOperationQueue.Enqueue(conference.conferenceDelegate.ParticipantJoined(participant));
    }

    internal static void ParticipantLeft(IntPtr opaque, IntPtr nativeParticipant)
    {
      Debug.Log($"Jitsi callback: participant_left opaque={opaque}");
      var conference = GCHandle.FromIntPtr(opaque).Target as Conference;
      var participant = new Participant(nativeParticipant);
      Jitsi.asyncOperationQueue.Enqueue(conference.conferenceDelegate.ParticipantLeft(participant));
    }

    internal static void ColibriMessageReceived(IntPtr opaque, IntPtr nativeColibriMessage)
    {
      Debug.Log($"Jitsi callback: colibri_message_received opaque={opaque}");
    }

    internal static void OfferReceived(IntPtr opaque, string sessionDescription, bool shouldSendAnswer)
    {
      Debug.Log($"Jitsi callback: offer_received opaque={opaque} shouldSendAnswer={shouldSendAnswer}");
      var conference = GCHandle.FromIntPtr(opaque).Target as Conference;
      Debug.Log("Starting negotiation");
      Jitsi.asyncOperationQueue.Enqueue(conference.Negotiation(sessionDescription, shouldSendAnswer));
    }
  }

  internal struct Agent
  {
    internal IntPtr opaque;
    internal NativeMethods.ParticipantJoined participant_joined;
    internal NativeMethods.ParticipantLeft participant_left;
    internal NativeMethods.ColibriMessageReceived colibri_message_received;
    internal NativeMethods.OfferReceived offer_received;
    internal NativeMethods.SessionTerminate session_terminate;
  }

  internal static class NativeMethods
  {
    [DllImport(Jitsi.Lib)]
    public static extern void jitsi_logging_init_stdout(
      [MarshalAs(UnmanagedType.LPUTF8Str)]
      string level
    );
    [DllImport(Jitsi.Lib)]
    public static extern bool jitsi_logging_init_file(
      [MarshalAs(UnmanagedType.LPUTF8Str)]
      string path,
      [MarshalAs(UnmanagedType.LPUTF8Str)]
      string level
    );
    [DllImport(Jitsi.Lib)]
    public static extern IntPtr jitsi_context_create();
    [DllImport(Jitsi.Lib)]
    public static extern void jitsi_context_free(IntPtr context);
    [DllImport(Jitsi.Lib)]
    public static extern IntPtr jitsi_connection_connect(
      IntPtr context,
      [MarshalAs(UnmanagedType.LPUTF8Str)]
      string websocketUrl,
      [MarshalAs(UnmanagedType.LPUTF8Str)]
      string xmppDomain,
      bool tlsInsecure
    );
    [DllImport(Jitsi.Lib)]
    public static extern void jitsi_connection_free(IntPtr connection);
    [DllImport(Jitsi.Lib)]
    public static extern IntPtr jitsi_connection_join(
      IntPtr context,
      IntPtr connection,
      [MarshalAs(UnmanagedType.LPUTF8Str)]
      string conferenceName,
      [MarshalAs(UnmanagedType.LPUTF8Str)]
      string nick,
      ref Agent agent
    );
    [DllImport(Jitsi.Lib)]
    public static extern IntPtr jitsi_conference_accept(
      IntPtr context,
      IntPtr conference,
      [MarshalAs(UnmanagedType.LPUTF8Str)]
      string sessionDescription
    );
    [DllImport(Jitsi.Lib)]
    public static extern StringHandle jitsi_conference_local_endpoint_id(IntPtr conference);
    [DllImport(Jitsi.Lib)]
    public static extern StringHandle jitsi_participant_jid(IntPtr participant);
    [DllImport(Jitsi.Lib)]
    public static extern StringHandle jitsi_participant_nick(IntPtr participant);
    [DllImport(Jitsi.Lib)]
    public static extern void jitsi_string_free(IntPtr s);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void ParticipantJoined(IntPtr opaque, IntPtr participant);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void ParticipantLeft(IntPtr opaque, IntPtr participant);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void ColibriMessageReceived(IntPtr opaque, IntPtr colibriMessage);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void OfferReceived(
      IntPtr opaque,
      [MarshalAs(UnmanagedType.LPUTF8Str)]
      string sessionDescription,
      bool shouldSendAnswer
    );
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void SessionTerminate(IntPtr opaque);
  }

  internal class StringHandle : SafeHandle
  {
    public StringHandle() : base(IntPtr.Zero, true) {}

    public override bool IsInvalid
    {
      get { return this.handle == IntPtr.Zero; }
    }

    public string AsString()
    {
      if (IsInvalid)
        return null;

      int len = 0;
      while (Marshal.ReadByte(handle, len) != 0) { ++len; }
      byte[] buffer = new byte[len];
      Marshal.Copy(handle, buffer, 0, buffer.Length);
      return Encoding.UTF8.GetString(buffer);
    }

    protected override bool ReleaseHandle()
    {
      if (!this.IsInvalid)
      {
        NativeMethods.jitsi_string_free(handle);
      }
      return true;
    }
  }
}
