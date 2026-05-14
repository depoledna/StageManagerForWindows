using System;
using System.Collections.Concurrent;
using System.Numerics;
using System.Threading;
using StageManager.Composition.Interop;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.UI.Composition;
using WinRT;

namespace StageManager.Composition
{
	/// <summary>
	/// Per-HWND Windows.Graphics.Capture session that draws each captured
	/// frame into a <see cref="CompositionDrawingSurface"/> via a zero-copy
	/// GPU blit. Frames arrive on a free-threaded callback; all D3D11 work
	/// runs on the capture thread under <see cref="_frameLock"/>.
	/// </summary>
	internal sealed class CaptureSession : IDisposable
	{
		// Per-hwnd lock — mirrors OpacityWindowStrategy's _windowLocks pattern
		// so concurrent Start/Pause/Resume/Dispose for the same target HWND
		// do not race on the framepool / session disposal.
		private static readonly ConcurrentDictionary<IntPtr, SemaphoreSlim> _hwndLocks = new();
		private static readonly Guid s_iidDxgiSurface = typeof(IDXGISurface).GUID;

		private readonly IntPtr _hwnd;
		private readonly Compositor _compositor;
		private readonly D3DDeviceHolder _devices;

		// Serialises ImmediateContext + drawing-surface BeginDraw/EndDraw.
		// ImmediateContext is single-threaded; capture frames could in theory
		// arrive concurrently if the framepool ever overlapped callbacks.
		private readonly object _frameLock = new();

		private GraphicsCaptureItem? _item;
		private Direct3D11CaptureFramePool? _framePool;
		private GraphicsCaptureSession? _session;
		private CompositionDrawingSurface? _surface;
		private ICompositionDrawingSurfaceInterop? _surfaceInterop;
		// _rootContainer: HWND-sized (oversized vs. base). Transform/Opacity
		// applied here so hover-scale grows the inner sprite outward but stays
		// inside the HWND rectangle (which would otherwise clip the rounded
		// corners of the inner sprite when scaled past base bounds).
		private ContainerVisual? _rootContainer;
		// _spriteVisual: base-sized, centered inside _rootContainer via Offset.
		// Owns the surface brush and rounded clip.
		private SpriteVisual? _spriteVisual;
		private CompositionSurfaceBrush? _surfaceBrush;
		private CompositionGeometricClip? _clip;
		private CompositionRoundedRectangleGeometry? _clipGeometry;
		private SizeInt32 _lastFrameSize;
		private SizeInt32 _lastSurfaceSize;
		private volatile bool _disposed;
		private volatile bool _paused;

		public Visual? RootVisual => _rootContainer;
		public event EventHandler? TargetClosed;

		public CaptureSession(IntPtr hwnd, Compositor compositor, D3DDeviceHolder devices)
		{
			_hwnd = hwnd;
			_compositor = compositor;
			_devices = devices;
		}

		public void Start()
		{
			var sem = _hwndLocks.GetOrAdd(_hwnd, _ => new SemaphoreSlim(1, 1));
			sem.Wait();
			try
			{
				if (_disposed) return;

				_item = GraphicsCaptureItemFactory.CreateForWindow(_hwnd);
				if (_item is null)
				{
					Log.Info("CAPSESS", $"CreateForWindow returned null for 0x{_hwnd:X}");
					return;
				}
				_item.Closed += OnItemClosed;

				// Seed the drawing surface at the item's known size when available
				// so the first frame has a correctly-sized backbuffer.
				var initialSize = (_item.Size.Width > 0 && _item.Size.Height > 0)
					? new Windows.Foundation.Size(_item.Size.Width, _item.Size.Height)
					: new Windows.Foundation.Size(1, 1);

				_surface = _devices.GraphicsDevice.CreateDrawingSurface(
					initialSize,
					DirectXPixelFormat.B8G8R8A8UIntNormalized,
					DirectXAlphaMode.Premultiplied);

				// CompositionDrawingSurface does not change identity across the
				// session lifetime — cache its IUnknown projection once instead
				// of QI'ing per frame.
				_surfaceInterop = ((object)_surface).As<ICompositionDrawingSurfaceInterop>();
				_lastSurfaceSize = new SizeInt32((int)initialSize.Width, (int)initialSize.Height);

				_surfaceBrush = _compositor.CreateSurfaceBrush(_surface);
				_surfaceBrush.Stretch = CompositionStretch.Uniform;
				_surfaceBrush.HorizontalAlignmentRatio = 0.5f;
				_surfaceBrush.VerticalAlignmentRatio = 0.5f;

				_spriteVisual = _compositor.CreateSpriteVisual();
				// DesktopWindowTarget root has no implicit parent size — the
				// host control drives explicit pixel size via SetVisualSize.
				_spriteVisual.Size = Vector2.Zero;
				_spriteVisual.Brush = _surfaceBrush;

				// Container is the externally-visible root. Hover/click scale +
				// opacity ride on it; the inner sprite stays centered inside.
				_rootContainer = _compositor.CreateContainerVisual();
				_rootContainer.Size = Vector2.Zero;
				_rootContainer.Children.InsertAtTop(_spriteVisual);

				BuildPool();
				Log.Info("CAPSESS", $"Started capture for 0x{_hwnd:X}");
			}
			finally
			{
				sem.Release();
			}
		}

		public void SetVisualSize(Vector2 hwndPixels, Vector2 basePixels)
		{
			lock (_frameLock)
			{
				if (_disposed) return;
				if (_rootContainer is not null)
					_rootContainer.Size = hwndPixels;
				if (_spriteVisual is not null)
				{
					_spriteVisual.Size = basePixels;
					// Center the base-sized sprite inside the oversized container.
					var off = (hwndPixels - basePixels) * 0.5f;
					_spriteVisual.Offset = new Vector3(off.X, off.Y, 0f);
				}
				if (_clipGeometry is not null)
					_clipGeometry.Size = basePixels;
			}
		}

		public void SetTransformMatrix(System.Numerics.Matrix4x4 transform)
		{
			lock (_frameLock)
			{
				if (_disposed || _rootContainer is null) return;
				_rootContainer.TransformMatrix = transform;
			}
		}

		public void SetOpacity(float opacity)
		{
			lock (_frameLock)
			{
				if (_disposed || _rootContainer is null) return;
				_rootContainer.Opacity = opacity;
			}
		}

		public void SetCornerRadius(float radiusPixels, Vector2 sizePixels)
		{
			lock (_frameLock)
			{
				if (_disposed || _spriteVisual is null) return;

				if (radiusPixels <= 0f)
				{
					_spriteVisual.Clip = null;
					_clip?.Dispose(); _clip = null;
					_clipGeometry?.Dispose(); _clipGeometry = null;
					return;
				}

				if (_clipGeometry is null)
				{
					_clipGeometry = _compositor.CreateRoundedRectangleGeometry();
					_clip = _compositor.CreateGeometricClip();
					_clip.Geometry = _clipGeometry;
					_spriteVisual.Clip = _clip;
				}
				_clipGeometry.CornerRadius = new Vector2(radiusPixels);
				_clipGeometry.Size = sizePixels;
			}
		}

		public void Pause()
		{
			if (_paused) return;
			var sem = _hwndLocks.GetOrAdd(_hwnd, _ => new SemaphoreSlim(1, 1));
			sem.Wait();
			try
			{
				if (_disposed || _paused) return;
				_paused = true;
				if (_session is not null) { _session.Dispose(); _session = null; }
				if (_framePool is not null)
				{
					_framePool.FrameArrived -= OnFrameArrived;
					_framePool.Dispose();
					_framePool = null;
				}
				Log.Info("CAPSESS", $"Paused capture for 0x{_hwnd:X}");
			}
			finally { sem.Release(); }
		}

		public void Resume()
		{
			if (!_paused) return;
			var sem = _hwndLocks.GetOrAdd(_hwnd, _ => new SemaphoreSlim(1, 1));
			sem.Wait();
			try
			{
				if (_disposed || _item is null) return;
				BuildPool();
				_paused = false;
				Log.Info("CAPSESS", $"Resumed capture for 0x{_hwnd:X}");
			}
			finally { sem.Release(); }
		}

		private void BuildPool()
		{
			if (_item is null) return;
			var size = _item.Size;
			if (size.Width <= 0 || size.Height <= 0) size = new SizeInt32(1, 1);

			_framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
				_devices.WinRTDevice,
				DirectXPixelFormat.B8G8R8A8UIntNormalized,
				numberOfBuffers: 2,
				size);
			_framePool.FrameArrived += OnFrameArrived;

			_session = _framePool.CreateCaptureSession(_item);
			// IsCursorCaptureEnabled has been on GraphicsCaptureSession since
			// 19H1 (target SDK floor). IsBorderRequired needs Win11 22H2 and a
			// QI to GraphicsCaptureSession2; skipped here to keep the floor.
			try { _session.IsCursorCaptureEnabled = false; } catch { }

			_session.StartCapture();
			_lastFrameSize = size;
		}

		private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
		{
			// Free-threaded callback — runs on a capture thread pool. Must
			// not touch DependencyProperties or any UI-thread state.
			if (_disposed || _paused) return;

			// Hold _frameLock across the entire frame lifecycle so a concurrent
			// Dispose() cannot tear down _surface / _framePool mid-blit.
			lock (_frameLock)
			{
				if (_disposed || _paused) return;

				using var frame = sender.TryGetNextFrame();
				if (frame is null) return;

				var contentSize = frame.ContentSize;
				if (contentSize.Width <= 0 || contentSize.Height <= 0) return;

				if (contentSize.Width != _lastFrameSize.Width || contentSize.Height != _lastFrameSize.Height)
				{
					try
					{
						sender.Recreate(_devices.WinRTDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, contentSize);
						_lastFrameSize = contentSize;
					}
					catch (Exception ex)
					{
						Log.Info("CAPSESS", $"Recreate framepool failed for 0x{_hwnd:X}: {ex.Message}");
						return;
					}
				}

				if (_surface is null || _surfaceInterop is null) return;

				if (contentSize.Width != _lastSurfaceSize.Width || contentSize.Height != _lastSurfaceSize.Height)
				{
					try
					{
						_surfaceInterop.Resize(new System.Drawing.Size(contentSize.Width, contentSize.Height));
						_lastSurfaceSize = contentSize;
					}
					catch (Exception ex)
					{
						Log.Info("CAPSESS", $"Surface resize failed: {ex.Message}");
						return;
					}
				}

				var iidDxgi = s_iidDxgiSurface;
				IntPtr pDxgiSurface;
				System.Drawing.Point offset;
				try
				{
					_surfaceInterop.BeginDraw(IntPtr.Zero, ref iidDxgi, out pDxgiSurface, out offset);
				}
				catch (Exception ex)
				{
					Log.Info("CAPSESS", $"BeginDraw failed: {ex.Message}");
					return;
				}

				try
				{
					// Vortice ComObject (IntPtr) ctor takes ownership of the
					// already-AddRef'd pointer returned by BeginDraw, mirror
					// of Direct3DInterop.GetTexture2DFromSurface.
					using var dstSurface = new IDXGISurface(pDxgiSurface);
					using var dstTex = dstSurface.QueryInterface<ID3D11Texture2D>();
					using var srcTex = Direct3DInterop.GetTexture2DFromSurface(frame.Surface);

					// Serialise immediate-context use across every CaptureSession
					// in the process — the D3D11 immediate context is single-threaded
					// and capture callbacks are free-threaded.
					lock (D3DDeviceHolder.ContextLock)
					{
						_devices.ImmediateContext.CopySubresourceRegion(
							dstTex, 0,
							(uint)offset.X, (uint)offset.Y, 0,
							srcTex, 0,
							null);
					}
				}
				catch (Exception ex)
				{
					Log.Info("CAPSESS", $"Frame blit failed for 0x{_hwnd:X}: {ex.Message}");
				}
				finally
				{
					try { _surfaceInterop.EndDraw(); }
					catch (Exception ex) { Log.Info("CAPSESS", $"EndDraw threw: {ex.Message}"); }
				}
			}
		}

		private void OnItemClosed(GraphicsCaptureItem sender, object args)
		{
			// GraphicsCaptureItem.Closed fires on a WinRT-internal thread holding
			// internal locks; calling Dispose() (which Waits on the per-hwnd
			// semaphore + _frameLock) synchronously here can deadlock against a
			// frame callback. Marshal off to the threadpool.
			TargetClosed?.Invoke(this, EventArgs.Empty);
			ThreadPool.QueueUserWorkItem(_ => Dispose());
		}

		public void Dispose()
		{
			if (_disposed) return;
			_disposed = true;

			var sem = _hwndLocks.TryGetValue(_hwnd, out var s) ? s : null;
			sem?.Wait();
			try
			{
				// Take _frameLock so an in-flight OnFrameArrived completes before
				// we dispose GPU resources. Unhook _item.Closed FIRST so disposing
				// the session can't synthesize a Closed callback that re-enters
				// Dispose.
				lock (_frameLock)
				{
					if (_item is not null) { _item.Closed -= OnItemClosed; }
					if (_session is not null) { _session.Dispose(); _session = null; }
					if (_framePool is not null)
					{
						_framePool.FrameArrived -= OnFrameArrived;
						_framePool.Dispose();
						_framePool = null;
					}
					_item = null;
					// Dispose container LAST among visuals — children are
					// disposed by the parent container automatically, but we
					// null our refs first so any racing callback short-circuits.
					_spriteVisual?.Dispose(); _spriteVisual = null;
					_surfaceBrush?.Dispose(); _surfaceBrush = null;
					_clip?.Dispose(); _clip = null;
					_clipGeometry?.Dispose(); _clipGeometry = null;
					_rootContainer?.Dispose(); _rootContainer = null;
					_surface?.Dispose(); _surface = null;
					_surfaceInterop = null;
				}
				Log.Info("CAPSESS", $"Disposed capture for 0x{_hwnd:X}");
			}
			finally
			{
				sem?.Release();
				// Drop the per-hwnd semaphore so the static dictionary doesn't
				// grow unbounded across short-lived windows.
				if (sem is not null && _hwndLocks.TryRemove(_hwnd, out var removed))
				{
					removed.Dispose();
				}
			}
		}
	}
}
