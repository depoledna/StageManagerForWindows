using System;
using System.Threading.Tasks;
using Windows.Graphics.Capture;
using Windows.Security.Authorization.AppCapabilityAccess;

namespace StageManager.Composition
{
	/// <summary>
	/// Suppresses the system capture indicator — the coloured rectangle Windows draws
	/// around any window that has a live Windows.Graphics.Capture session.
	/// <para>
	/// Every tray tile runs a session, so the border is on constantly; it just sits
	/// off-screen with the parked window and is never seen. It becomes visible the moment
	/// a captured window comes back on screen while its session is still running — the
	/// sidebar drag past the buffer shows the real window at the cursor, and a scene
	/// switch captures the outgoing window while it is still on stage.
	/// </para>
	/// <para>
	/// Turning it off needs user consent (<see cref="GraphicsCaptureAccess"/>), which is
	/// granted once per process and applies to every session started afterwards. Sessions
	/// started before <see cref="RequestAsync"/> completes keep their border until the
	/// framepool is next rebuilt — harmless, because those are the resting tray tiles.
	/// </para>
	/// </summary>
	internal static class CaptureBorder
	{
		// Written on the UI thread by RequestAsync, read from capture threads in Apply.
		private static volatile bool _allowed;

		/// <summary>
		/// Asks for borderless-capture consent. Safe to call on a system that has no such
		/// API — everything below the Windows 10 2104 (20348) floor simply stays bordered.
		/// </summary>
		public static async Task RequestAsync()
		{
			if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 20348))
			{
				Log.Info("CAPSESS", "Borderless capture unavailable on this Windows build");
				return;
			}

			try
			{
				var status = await GraphicsCaptureAccess.RequestAccessAsync(GraphicsCaptureAccessKind.Borderless);
				_allowed = status == AppCapabilityAccessStatus.Allowed;
				Log.Info("CAPSESS", $"Borderless capture access: {status}");
			}
			catch (Exception ex)
			{
				// Denied consent is a status, not a throw — this is the API being absent
				// or brokered away, and a visible border is a cosmetic loss either way.
				Log.Info("CAPSESS", $"Borderless capture request failed: {ex.Message}");
			}
		}

		/// <summary>
		/// Drops the indicator for one session. Without consent the setter still succeeds
		/// and is then ignored by the system, so the <see cref="_allowed"/> check is what
		/// keeps the log honest rather than what makes it work.
		/// </summary>
		public static void Apply(GraphicsCaptureSession session)
		{
			// The version test is redundant with _allowed, which can only be set after the
			// same test passed — but CA1416 reasons per method, so it has to be restated.
			if (!_allowed || !OperatingSystem.IsWindowsVersionAtLeast(10, 0, 20348)) return;

			try { session.IsBorderRequired = false; }
			catch (Exception ex) { Log.Info("CAPSESS", $"IsBorderRequired failed: {ex.Message}"); }
		}
	}
}
