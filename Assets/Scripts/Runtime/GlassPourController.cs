using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace GlassPourDemo
{
    public sealed class GlassPourController : MonoBehaviour
    {
        public Sprite frameFx;
        public float pourDuration = 1.35f;
        public float tiltAngle = -64f;

        private GlassVessel source;
        private GlassVessel target;
        private LineRenderer stream;
        private bool busy;
        private Color activePourColor;

        private readonly Color purple = new Color32(104, 47, 148, 255);
        private readonly Color pink = new Color32(255, 129, 219, 255);
        private readonly Color orange = new Color32(232, 100, 0, 255);

        private void Start()
        {
            source = CreateGlass("SourceGlass", new Vector3(-2.1f, 0.1f, 0f));
            target = CreateGlass("TargetGlass", new Vector3(2.1f, 0.1f, 0f));
            source.layers = new List<LiquidLayer>
            {
                new LiquidLayer(purple, 0.24f),
                new LiquidLayer(pink, 0.24f),
                new LiquidLayer(orange, 0.24f)
            };
            target.layers = new List<LiquidLayer>();
            CreateStream();
            CreateInstruction();
        }

        private GlassVessel CreateGlass(string objectName, Vector3 position)
        {
            var go = new GameObject(objectName);
            go.transform.position = position;
            var vessel = go.AddComponent<GlassVessel>();
            vessel.frameFx = frameFx;
            vessel.BuildVisuals();
            return vessel;
        }

        private void Update()
        {
            if (!busy && (Input.GetMouseButtonDown(0) || Input.GetKeyDown(KeyCode.Space)))
                StartCoroutine(PourTopLayer());
        }

        private IEnumerator PourTopLayer()
        {
            if (source.layers.Count == 0) yield break;
            busy = true;
            int sourceIndex = source.layers.Count - 1;
            LiquidLayer moving = source.layers[sourceIndex];
            activePourColor = moving.color;
            float originalAmount = moving.fraction;
            target.layers.Add(new LiquidLayer(moving.color, 0f));
            int targetIndex = target.layers.Count - 1;

            Vector3 startPosition = source.transform.position;
            Vector3 pourPosition = target.transform.position + new Vector3(-0.85f, 2.05f, 0f);
            source.SetForeground(true);
            yield return AnimatePose(startPosition, pourPosition, 0f, tiltAngle, 0.55f);
            stream.enabled = true;

            for (float elapsed = 0f; elapsed < pourDuration; elapsed += Time.deltaTime)
            {
                float t = Mathf.SmoothStep(0f, 1f, elapsed / pourDuration);
                LiquidLayer a = source.layers[sourceIndex];
                a.fraction = originalAmount * (1f - t);
                source.layers[sourceIndex] = a;
                LiquidLayer b = target.layers[targetIndex];
                b.fraction = originalAmount * t;
                target.layers[targetIndex] = b;
                UpdateStream();
                yield return null;
            }

            LiquidLayer finalTarget = target.layers[targetIndex];
            finalTarget.fraction = originalAmount;
            target.layers[targetIndex] = finalTarget;
            source.layers.RemoveAt(sourceIndex);
            stream.enabled = false;
            yield return AnimatePose(pourPosition, startPosition, tiltAngle, 0f, 0.55f);
            source.SetForeground(false);
            busy = false;
        }

        private IEnumerator AnimatePose(Vector3 from, Vector3 to, float fromAngle, float toAngle, float duration)
        {
            for (float elapsed = 0f; elapsed < duration; elapsed += Time.deltaTime)
            {
                float t = Mathf.SmoothStep(0f, 1f, elapsed / duration);
                source.transform.position = Vector3.Lerp(from, to, t);
                source.transform.rotation = Quaternion.Euler(0f, 0f, Mathf.Lerp(fromAngle, toAngle, t));
                yield return null;
            }
            source.transform.position = to;
            source.transform.rotation = Quaternion.Euler(0f, 0f, toAngle);
        }

        private void CreateStream()
        {
            var go = new GameObject("PourStream");
            stream = go.AddComponent<LineRenderer>();
            stream.material = new Material(Shader.Find("Sprites/Default"));
            stream.startWidth = 0.16f;
            stream.endWidth = 0.11f;
            stream.positionCount = 2;
            stream.numCapVertices = 6;
            stream.sortingOrder = 12;
            stream.enabled = false;
        }

        private void UpdateStream()
        {
            stream.startColor = activePourColor;
            stream.endColor = activePourColor;
            stream.SetPosition(0, source.transform.TransformPoint(new Vector3(1.12f, 1.10f, 0f)));
            stream.SetPosition(1, target.transform.position + new Vector3(0f, 1.14f, 0f));
        }

        private void CreateInstruction()
        {
            var go = new GameObject("Instruction");
            go.transform.position = new Vector3(0f, -3.35f, 0f);
            var text = go.AddComponent<TextMesh>();
            text.text = "DÖKMEK İÇİN TIKLA / SPACE";
            text.anchor = TextAnchor.MiddleCenter;
            text.alignment = TextAlignment.Center;
            text.characterSize = 0.13f;
            text.fontSize = 48;
            text.color = new Color(0.7f, 0.85f, 1f, 1f);
        }
    }
}
