using System;
using BoneLib;
using UnityEngine;
using Object = UnityEngine.Object;

namespace GravityGloves
{
    // A looping sound at a moving point, like the hum while a glove holds a lock. Its volume follows the
    // requested one smoothly, and it stops playing once it has faded out.
    internal sealed class LoopSound
    {
        private const float Attack = 0.05f;

        private readonly string _name;
        private readonly Func<AudioClip> _clip;
        private readonly float _doppler;
        private AudioSource _source;
        private AudioClip _playing;
        private bool _unavailable;
        private float _level;
        private float _pitch = 1f;
        private Vector3 _position;

        // Doppler is how much the pitch shifts as it moves toward or away from the player (1 is realistic).
        public LoopSound(string name, Func<AudioClip> clip, float doppler)
        {
            _name = name;
            _clip = clip;
            _doppler = doppler;
        }

        public bool IsSilent => _level <= 0f;

        // A recording to loop instead of the built-in clip, picked when the sound starts.
        public RecordedSound Recording { get; set; }

        // The recording's volume, to scale the requested volume by.
        public float RecordingVolume => Recording != null && Recording.Clip != null ? Recording.Volume : 1f;

        // Call every frame. Release is how quickly it fades when the volume drops, in seconds.
        public void Update(Vector3 position, float volume, float pitch, float release)
        {
            _position = position;
            _pitch = pitch;
            float dt = Time.unscaledDeltaTime;
            float smoothing = volume > _level ? Attack : release;
            _level += (volume - _level) * (1f - Mathf.Exp(-dt / Mathf.Max(smoothing, 0.001f)));
            if (volume <= 0f && _level < 0.002f)
            {
                Silence();
                return;
            }
            if (!EnsureCreated())
                return;

            AudioClip clip = Recording != null && Recording.Clip != null ? Recording.Clip : _clip();
            if (clip == null)
            {
                Silence();
                return;
            }
            if (!ReferenceEquals(clip, _playing))
            {
                _playing = clip;
                _source.Stop();
                _source.clip = clip;
            }
            _source.transform.position = position;
            _source.volume = Mathf.Clamp01(_level);
            _source.pitch = pitch;
            if (!_source.isPlaying)
            {
                // A recording starts anywhere in it, so its quiet start (the hum's is 16-21 dB below its peak) isn't
                // all a short lock ever plays.
                if (Recording != null && Recording.Clip != null)
                    _source.timeSamples = UnityEngine.Random.Range(0, Recording.Clip.samples);
                _source.Play();
            }
        }

        // Fades out from wherever it last was.
        public void FadeOut(float release) => Update(_position, 0f, _pitch, release);

        public void Silence()
        {
            _level = 0f;
            if (_source != null && _source.isPlaying)
                _source.Stop();
        }

        private bool EnsureCreated()
        {
            if (_unavailable)
                return false;
            if (_source != null)
                return true;
            try
            {
                var soundObject = new GameObject(_name);
                Object.DontDestroyOnLoad(soundObject);
                _source = soundObject.AddComponent<AudioSource>();
                _source.loop = true;
                _source.playOnAwake = false;
                _source.spatialBlend = 1f;
                _source.rolloffMode = AudioRolloffMode.Logarithmic;
                _source.minDistance = 0.5f;
                _source.maxDistance = 30f;
                _source.dopplerLevel = _doppler;
                _source.outputAudioMixerGroup = Audio.SoftInteraction;
                return true;
            }
            catch (Exception e)
            {
                _unavailable = true;
                Sounds.LogErrorOnce(e);
                return false;
            }
        }
    }
}
