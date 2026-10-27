// Plays audio and triggers haptic buzz when the needle tip enters a wrong-hit collider.
// For muscle/cord detection (voxel-based), HybridNeedleController calls StartBuzz/StopBuzz
// and drives the voxelBuzzAudio AudioSource directly.

using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace SpineVoxels.Runtime.Haptic
{
    [AddComponentMenu("SpineVoxels/Needle Collider Audio")]
    public class NeedleColliderAudio : MonoBehaviour
    {
        [Tooltip("AudioSource configured with the wrong-hit clip.")]
        public AudioSource audioSource;

        [Tooltip("Colliders that trigger the sound (must have Is Trigger enabled).")]
        public Collider[] targets;

        [Range(0f, 0.5f)]
        [Tooltip("Fade-out before restarting if already playing.")]
        public float fadeOutTime = 0.15f;

        [Header("Haptic buzz (optional)")]
        [Tooltip("HybridNeedleController to vibrate when a wrong-hit collider is touched.")]
        public HybridNeedleController hapticController;

        [Range(1f, 500f)]  public float buzzFrequency = 30f;
        [Range(0f, 9f)]    public float buzzFMin      = 0f;
        [Range(0f, 9f)]    public float buzzFMax      = 3f;

        Coroutine              _fade;
        readonly HashSet<Collider> _touching = new();

        void OnTriggerEnter(Collider other)  => OnEnter(other);
        void OnTriggerExit(Collider other)   => OnExit(other);
        void OnCollisionEnter(Collision col) => OnEnter(col.collider);
        void OnCollisionExit(Collision col)  => OnExit(col.collider);

        bool IsTarget(Collider c)
        {
            foreach (var t in targets)
                if (t == c) return true;
            return false;
        }

        void OnEnter(Collider other)
        {
            if (!IsTarget(other)) return;
            PlayAudio();
            _touching.Add(other);
            hapticController?.StartBuzz(buzzFrequency, buzzFMin, buzzFMax);
        }

        void OnExit(Collider other)
        {
            if (!_touching.Remove(other)) return;
            if (_touching.Count == 0)
                hapticController?.StopBuzz();
        }

        void OnDisable()
        {
            _touching.Clear();
            hapticController?.StopBuzz();
        }

        void PlayAudio()
        {
            if (audioSource == null) return;
            if (_fade != null) StopCoroutine(_fade);
            if (audioSource.isPlaying) _fade = StartCoroutine(FadeAndRestart());
            else Play();
        }

        void Play() { audioSource.volume = 1f; audioSource.Play(); }

        IEnumerator FadeAndRestart()
        {
            float startVol = audioSource.volume;
            float t = 0f;
            while (t < fadeOutTime)
            {
                t += Time.deltaTime;
                audioSource.volume = Mathf.Lerp(startVol, 0f, t / fadeOutTime);
                yield return null;
            }
            Play();
            _fade = null;
        }
    }
}
