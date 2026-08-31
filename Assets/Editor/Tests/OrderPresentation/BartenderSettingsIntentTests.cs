using System;
using System.Reflection;
using LiquidSort.Levels;
using NUnit.Framework;

namespace LiquidSort.Tests.EditMode
{
    public sealed class BartenderSettingsIntentTests
    {
        private const BindingFlags HandlerFlags =
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        [TestCase(typeof(BartenderMainMenuPresenter))]
        [TestCase(typeof(BartenderPausePresenter))]
        public void Vibration_handler_calls_setting_store_and_never_hard_resets(
            Type presenterType)
        {
            MethodInfo handler = presenterType.GetMethod("ToggleVibration", HandlerFlags);
            MethodInfo toggle = typeof(BartenderSettingsStore).GetMethod(
                nameof(BartenderSettingsStore.ToggleVibration),
                BindingFlags.Static | BindingFlags.Public);
            MethodInfo hardReset = typeof(BartenderProgressService).GetMethod(
                nameof(BartenderProgressService.HardReset),
                BindingFlags.Static | BindingFlags.Public);

            Assert.That(handler, Is.Not.Null);
            Assert.That(toggle, Is.Not.Null);
            Assert.That(hardReset, Is.Not.Null);
            Assert.That(Calls(handler, toggle), Is.True,
                presenterType.Name + " titreşim ayarını değiştirmiyor.");
            Assert.That(Calls(handler, hardReset), Is.False,
                presenterType.Name + " titreşim niyetini veri sıfırlamaya yönlendiriyor.");
        }

        private static bool Calls(MethodInfo caller, MethodInfo expectedCallee)
        {
            byte[] il = caller.GetMethodBody()?.GetILAsByteArray();
            if (il == null) return false;

            // Both targets are static methods, so the C# compiler emits the one-byte
            // `call` opcode (0x28) followed by a four-byte metadata token.
            for (int i = 0; i <= il.Length - 5; i++)
            {
                if (il[i] != 0x28) continue;
                int token = BitConverter.ToInt32(il, i + 1);
                try
                {
                    MethodBase resolved = caller.Module.ResolveMethod(token);
                    if (resolved != null
                        && resolved.Module == expectedCallee.Module
                        && resolved.MetadataToken == expectedCallee.MetadataToken)
                        return true;
                }
                catch (ArgumentException)
                {
                    // A 0x28 byte inside another instruction's operand is not a call.
                }
            }
            return false;
        }
    }
}
