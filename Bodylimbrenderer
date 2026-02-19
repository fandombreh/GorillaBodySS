using UnityEngine;

namespace GorillaBodyServer
{
    /// <summary>
    /// Renders ghost chest and elbow limbs for a remote player.
    /// Visible to ALL players (no mod required on their end).
    /// </summary>
    public class BodyLimbRenderer : MonoBehaviour
    {
        private Transform _chest;
        private Transform _leftElbow;
        private Transform _rightElbow;

        // Smoothing speed for interpolation
        private const float LerpSpeed = 15f;

        private BodyTrackingData _targetData;

        private void Awake()
        {
            _chest = CreateLimb("Chest", new Vector3(0.3f, 0.5f, 0.15f), Color.white);
            _leftElbow = CreateLimb("LeftElbow", new Vector3(0.1f, 0.3f, 0.1f), new Color(0.8f, 0.8f, 0.8f));
            _rightElbow = CreateLimb("RightElbow", new Vector3(0.1f, 0.3f, 0.1f), new Color(0.8f, 0.8f, 0.8f));
        }

        private Transform CreateLimb(string limbName, Vector3 scale, Color color)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            go.name = limbName;
            go.transform.SetParent(transform);
            go.transform.localScale = scale;

            // Remove collider so it doesn't interfere with physics
            var col = go.GetComponent<Collider>();
            if (col != null) Destroy(col);

            // Set color using a new material
            var renderer = go.GetComponent<Renderer>();
            if (renderer != null)
            {
                var mat = new Material(Shader.Find("Universal Render Pipeline/Lit"));
                mat.color = color;
                renderer.material = mat;
            }

            return go.transform;
        }

        public void UpdateLimbs(BodyTrackingData data)
        {
            _targetData = data;
        }

        private void Update()
        {
            if (_targetData == null) return;

            // Smoothly interpolate limb positions/rotations
            _chest.position = Vector3.Lerp(_chest.position, _targetData.ChestPosition, Time.deltaTime * LerpSpeed);
            _chest.rotation = Quaternion.Slerp(_chest.rotation, _targetData.ChestRotation, Time.deltaTime * LerpSpeed);

            _leftElbow.position = Vector3.Lerp(_leftElbow.position, _targetData.LeftElbowPosition, Time.deltaTime * LerpSpeed);
            _leftElbow.rotation = Quaternion.Slerp(_leftElbow.rotation, _targetData.LeftElbowRotation, Time.deltaTime * LerpSpeed);

            _rightElbow.position = Vector3.Lerp(_rightElbow.position, _targetData.RightElbowPosition, Time.deltaTime * LerpSpeed);
            _rightElbow.rotation = Quaternion.Slerp(_rightElbow.rotation, _targetData.RightElbowRotation, Time.deltaTime * LerpSpeed);
        }
    }
}
