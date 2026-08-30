using UnityEngine;

namespace LiquidSort.Levels
{
    /// <summary>
    /// Bir World Space veya Screen Space - Camera <see cref="Canvas"/>'ın kamerasını
    /// çalışma anında çözer.
    ///
    /// Kamera bilerek rig'in DIŞINDA duruyor — taşınabilir prefab kendi kamerasını
    /// getirmez, hedef sahnenin kamerasını kullanır. Ama World Space bir canvas
    /// dokunuşları ancak bir kamera bildiği zaman okur. Referansı sahnede yazmak
    /// prefab kaydedilirken kopardığı için (kök dışına bakan bir bağ prefab'a
    /// giremez) tek doğru yer burası.
    ///
    /// <see cref="BartenderPourInteraction"/> kendi kamerasını aynı biçimde çözüyor;
    /// disiplin bilerek aynı.
    /// </summary>
    [ExecuteAlways]
    [DisallowMultipleComponent]
    [RequireComponent(typeof(Canvas))]
    public sealed class WorldCanvasCameraBinder : MonoBehaviour
    {
        [Tooltip("Doluysa doğrudan bu kullanılır. Boşken Camera.main çözülür.")]
        [SerializeField] private Camera explicitCamera;

        private Canvas canvas;

        public Camera ResolvedCamera => canvas != null ? canvas.worldCamera : null;

        private void OnEnable()
        {
            canvas = GetComponent<Canvas>();
            Bind();
        }

        private void Update()
        {
            // Sahne değişebilir, kamera yeniden yaratılabilir. Karşılaştırma bir referans
            // testi; her karede yapılması bedava.
            if (canvas != null && canvas.worldCamera != null) return;
            Bind();
        }

        /// <summary>Authoring API for an editor builder.</summary>
        public void ConfigureSceneBindings(Camera camera)
        {
            explicitCamera = camera;
            if (isActiveAndEnabled) Bind();
        }

        private void Bind()
        {
            if (canvas == null) canvas = GetComponent<Canvas>();
            if (canvas == null || canvas.renderMode != RenderMode.WorldSpace) return;
            Camera resolved = explicitCamera != null ? explicitCamera : Camera.main;
            if (resolved != null) canvas.worldCamera = resolved;
        }
    }
}
