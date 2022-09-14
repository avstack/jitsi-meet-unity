using AVStack.Jitsi;
using System.Collections;
using System.Collections.Generic;
using Unity.WebRTC;
using UnityEngine;
using UnityEngine.UI;

public class SampleBehaviour : MonoBehaviour
{
    [SerializeField]
    private GameObject canvasGameObject;

    private Tile localTile;
    private int tileIndex;
    private Dictionary<string, Tile> remoteTiles;

    private Connection connection;
    private Conference conference;

    private WebCamTexture webCamTexture;
    private AudioSource microphoneSource;
    private VideoStreamTrack localVideoTrack;
    private AudioStreamTrack localAudioTrack;

    internal struct Tile
    {
        private GameObject gameObject;
        private RawImage rawImage;

        internal Tile(Transform parent, Texture texture, int index)
        {
            this.gameObject = new GameObject();
            this.gameObject.transform.parent = parent;

            this.rawImage = this.gameObject.AddComponent<RawImage>();
            this.rawImage.texture = texture;

            // var transform = this.rawImage.GetComponent<RectTransform>();
        }
    }

    private class ConferenceDelegate : IConferenceDelegate
    {
        private SampleBehaviour behaviour;

        internal ConferenceDelegate(SampleBehaviour behaviour)
        {
            this.behaviour = behaviour;
        }

        public IEnumerator ParticipantJoined(Participant participant)
        {
            Debug.Log($"Participant joined: {participant}");
            yield return null;
        }

        public IEnumerator ParticipantLeft(Participant participant)
        {
            Debug.Log($"Participant left: {participant}");
            yield return null;
        }

        public IEnumerator RemoteAudioTrackAdded(Participant participant, AudioStreamTrack audioTrack)
        {
            Debug.Log($"Remote audio track added: {audioTrack.Id}");
            // audioTrack.source is an AudioSource
            yield return null;
        }

        public IEnumerator RemoteAudioTrackRemoved(Participant participant, AudioStreamTrack audioTrack)
        {
            Debug.Log($"Remote audio track removed: {audioTrack.Id}");
            yield return null;
        }

        public IEnumerator RemoteVideoTrackAdded(Participant participant, VideoStreamTrack videoTrack)
        {
            Debug.Log($"Remote video track added: {videoTrack.Id}");
            // We defer adding the tile until we get the video texture in VideoReceived
            yield return null;
        }

        public IEnumerator RemoteVideoTrackRemoved(Participant participant, VideoStreamTrack videoTrack)
        {
            Debug.Log($"Remote video track removed: {videoTrack.Id}");
            this.behaviour.RemoveRemoteTile(videoTrack.Id);
            yield return null;
        }

        public IEnumerator VideoReceived(Participant participant, VideoStreamTrack videoTrack, Texture texture)
        {
            Debug.Log("Video received");
            this.behaviour.AddRemoteTile(trackId, texture);
            yield return null;
        }

        public IEnumerator SessionTerminate()
        {
            this.behaviour.remoteTiles.Clear();
            this.behaviour.tileIndex = 1;
            yield return null;
        }
    }

    void Awake()
    {
        Debug.Log("Initialising Jitsi");
        Jitsi.Initialize();
    }

    void OnDestroy()
    {
        Jitsi.Dispose();
    }

    void Start()
    {
        Debug.Log("Starting Jitsi background task");
        StartCoroutine(Jitsi.Update(this));

        StartCoroutine(this.JoinConference());
    }

    void AddRemoteTile(string trackId, Texture texture)
    {
        Debug.Log($"Adding tile for remote video stream: {trackId}");
        this.remoteTiles.Add(trackId, new Tile(this.canvasGameObject.transform, texture, this.tileIndex++));
    }

    void RemoveRemoteTile(string trackId)
    {
        Debug.Log($"Removing tile for remote video stream: {trackId}");

        // var tile;
        if (this.remoteTiles.Remove(trackId))
        {
            // tile.RemoveFromParent();
        }
    }

    IEnumerator JoinConference()
    {
        Debug.Log("Starting webcam");
        this.webCamTexture = new WebCamTexture(640, 360);
        this.webCamTexture.Play();

        Debug.Log("Starting microphone");
        this.microphoneSource = gameObject.AddComponent<AudioSource>();
        this.microphoneSource.clip = Microphone.Start(null, true, 10, 48000);
        this.microphoneSource.Play();

        // Unity's webcam texture reports 16x16 until capture has finished starting,
        // which happens asynchronously.
        // 
        // If it's assigned to the local track while it still reports 16x16, then
        // 16x16 will be the transmitted resolution!
        Debug.Log("Waiting for webcam to finish starting");
        yield return new WaitUntil(() => this.webCamTexture.width != 16);

        this.tileIndex = 0;
        this.localTile = new Tile(this.canvasGameObject.transform, this.webCamTexture, this.tileIndex++);
        this.remoteTiles = new Dictionary<string, Tile>();

        this.localVideoTrack = new VideoStreamTrack(this.webCamTexture, true);
        this.localAudioTrack = new AudioStreamTrack(this.microphoneSource);
        var localTracks = new MediaStreamTrack[] { this.localVideoTrack, this.localAudioTrack };

        Debug.Log("Connecting");
        // new Connection() currently blocks until the connection is established.
        // This will be changed to be asynchronous in future
        this.connection = new Connection("wss://example.com/xmpp-websocket", "example.com", false);

        Debug.Log("Joining conference");
        // join() currently blocks until the room is joined.
        // This will be changed to be asynchronous in future
        this.conference = this.connection.join("nativeroom", "unitynick", localTracks, new ConferenceDelegate(this));

        var localEndpointId = this.conference.LocalEndpointId();
        Debug.Log($"Joined. My endpoint ID: {localEndpointId}");
    }
}
