using System;
using System.Runtime.InteropServices;
using StageManager.Composition.Interop;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Foundation;
using Windows.UI.Composition;
using WinRT;
using WinRTDirect3D11 = Windows.Graphics.DirectX.Direct3D11;

namespace StageManager.Composition
{
	/// <summary>
	/// Process-wide owner of the shared D3D11 device used by every capture
	/// session and composition surface. Wraps:
	///   * a Vortice <see cref="ID3D11Device"/> + immediate context
	///   * the same device projected as WinRT
	///     <see cref="WinRTDirect3D11.IDirect3DDevice"/> for
	///     <c>Direct3D11CaptureFramePool</c>
	///   * a <see cref="Windows.UI.Composition.CompositionGraphicsDevice"/>
	///     bound to the compositor for drawing surfaces
	/// Subscribes to <c>RenderingDeviceReplaced</c> so DWM-induced device loss
	/// triggers an automatic rebuild; live capture sessions react via the
	/// public <see cref="DeviceLost"/> event.
	/// </summary>
	internal sealed class D3DDeviceHolder : IDisposable
	{
		private static readonly object _lock = new();
		private static D3DDeviceHolder? _instance;

		/// <summary>
		/// Process-wide gate for ID3D11DeviceContext use. The immediate context is
		/// single-threaded; every BeginDraw/CopySubresourceRegion/EndDraw chain
		/// across all CaptureSessions must hold this lock.
		/// </summary>
		public static readonly object ContextLock = new();

		/// <summary>
		/// Returns the singleton, lazily constructing it on first call. The
		/// supplied compositor is captured for the lifetime of the process —
		/// subsequent calls ignore the argument.
		/// </summary>
		public static D3DDeviceHolder GetOrCreate(Compositor compositor)
		{
			if (compositor is null)
				throw new ArgumentNullException(nameof(compositor));

			lock (_lock)
			{
				return _instance ??= new D3DDeviceHolder(compositor);
			}
		}

		public ID3D11Device D3DDevice { get; private set; } = null!;
		public ID3D11DeviceContext ImmediateContext { get; private set; } = null!;
		public WinRTDirect3D11.IDirect3DDevice WinRTDevice { get; private set; } = null!;
		public CompositionGraphicsDevice GraphicsDevice { get; private set; } = null!;

		/// <summary>
		/// Raised after the underlying D3D11 device has been recreated
		/// following device loss. Listeners (capture sessions, surfaces)
		/// should drop and rebuild any GPU resources they held.
		/// </summary>
		public event EventHandler? DeviceLost;

		private readonly Compositor _compositor;
		private TypedEventHandler<CompositionGraphicsDevice, RenderingDeviceReplacedEventArgs>? _renderingReplacedHandler;
		private bool _disposed;

		private D3DDeviceHolder(Compositor compositor)
		{
			_compositor = compositor;
			Build();
		}

		private void Build()
		{
			// 1) Try a hardware device with BGRA support (required for
			//    composition interop). Fall back to WARP (still GPU-pipelined
			//    on the software rasteriser) if hardware creation fails.
			var flags = DeviceCreationFlags.BgraSupport;
			var featureLevels = new[]
			{
				FeatureLevel.Level_11_1,
				FeatureLevel.Level_11_0,
				FeatureLevel.Level_10_1,
				FeatureLevel.Level_10_0,
			};

			ID3D11Device device;
			ID3D11DeviceContext context;

			try
			{
				D3D11.D3D11CreateDevice(
					adapter: null,
					DriverType.Hardware,
					flags,
					featureLevels,
					out device,
					out context).CheckError();
				Log.Info("D3DDEV", "Created hardware D3D11 device");
			}
			catch (Exception hwEx)
			{
				Log.Info("D3DDEV", $"Hardware D3D11 device creation failed: {hwEx.Message} — falling back to WARP");
				D3D11.D3D11CreateDevice(
					adapter: null,
					DriverType.Warp,
					flags,
					featureLevels,
					out device,
					out context).CheckError();
				Log.Info("D3DDEV", "Created WARP D3D11 device");
			}

			D3DDevice = device;
			ImmediateContext = context;

			// 2) Same D3D11 device, viewed as IDXGIDevice — needed by
			//    CreateDirect3D11DeviceFromDXGIDevice.
			using var dxgiDevice = D3DDevice.QueryInterface<IDXGIDevice>();

			// 3) WinRT projection of the device for capture/composition.
			WinRTDevice = Direct3DInterop.CreateDirect3DDevice(dxgiDevice);

			// 4) CompositionGraphicsDevice — feeds CreateDrawingSurface and
			//    fires RenderingDeviceReplaced on DWM device loss.
			//    ICompositorInterop::CreateGraphicsDevice wants a raw D3D/D2D
			//    device IUnknown — NOT the WinRT IDirect3DDevice projection.
			//    The result is an IInspectable* we unwrap manually because
			//    CsWinRT 2.x's projected-type marshal is broken in COM ABI
			//    signatures (see CompositionInterop.cs).
			var interop = _compositor.As<ICompositorInterop>();
			interop.CreateGraphicsDevice(D3DDevice.NativePointer, out IntPtr gdAbi);
			try
			{
				GraphicsDevice = MarshalInspectable<CompositionGraphicsDevice>.FromAbi(gdAbi);
			}
			finally
			{
				MarshalInspectable<CompositionGraphicsDevice>.DisposeAbi(gdAbi);
			}

			_renderingReplacedHandler = OnRenderingDeviceReplaced;
			GraphicsDevice.RenderingDeviceReplaced += _renderingReplacedHandler;
		}

		private void OnRenderingDeviceReplaced(CompositionGraphicsDevice sender, RenderingDeviceReplacedEventArgs args)
		{
			if (_disposed) return;
			Log.Info("D3DDEV", "RenderingDeviceReplaced fired — rebuilding D3D11 device");
			TeardownDevice();
			Build();
			DeviceLost?.Invoke(this, EventArgs.Empty);
		}

		private void TeardownDevice()
		{
			if (_renderingReplacedHandler is not null && GraphicsDevice is not null)
			{
				try { GraphicsDevice.RenderingDeviceReplaced -= _renderingReplacedHandler; }
				catch (Exception ex) { Log.Info("D3DDEV", $"Unsubscribe RenderingDeviceReplaced threw: {ex.Message}"); }
			}
			_renderingReplacedHandler = null;

			// Release in reverse order of creation; swallow individual
			// failures so a single bad release never blocks the rest.
			SafeDispose(GraphicsDevice, "GraphicsDevice");
			SafeDispose(WinRTDevice, "WinRTDevice");
			SafeDispose(ImmediateContext, "ImmediateContext");
			SafeDispose(D3DDevice, "D3DDevice");

			GraphicsDevice = null!;
			WinRTDevice = null!;
			ImmediateContext = null!;
			D3DDevice = null!;
		}

		private static void SafeDispose(object? obj, string tag)
		{
			if (obj is null)
				return;
			try
			{
				if (obj is IDisposable disposable)
					disposable.Dispose();
			}
			catch (Exception ex)
			{
				Log.Info("D3DDEV", $"Dispose of {tag} threw: {ex.Message}");
			}
		}

		public void Dispose()
		{
			if (_disposed)
				return;
			_disposed = true;

			TeardownDevice();

			lock (_lock)
			{
				if (ReferenceEquals(_instance, this))
					_instance = null;
			}
		}
	}
}
