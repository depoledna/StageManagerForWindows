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

		// Upper bound on every semaphore wait. Nothing under this lock is long-running
		// (framepool build / WinRT dispose), so exceeding it means the holder is wedged
		// — proceeding or bailing beats hanging the thread forever.
		private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(2);

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
		// _rootContainer: HWND-sized. Holds ONLY a pure perspective matrix (the
		// "camera") so it foreshortens the sprite's native Y-rotation into the
		// resting trapezoid. Opacity rides here too.
		private ContainerVisual? _rootContainer;
		// _contentVisual: HWND-sized child of the root. Carries the affine content
		// transform (hover scale / per-row shear) — kept OFF the root
		// so the root's matrix stays pure perspective (an affine+perspective matrix
		// on one visual collapses to affine and never divides).
		private ContainerVisual? _contentVisual;
		// _spriteVisual: base-sized, centered inside _contentVisual via Offset.
		// Owns the surface brush + rounded clip; carries the native Y-rotation.
		private SpriteVisual? _spriteVisual;
		private CompositionSurfaceBrush? _surfaceBrush;
		private CompositionGeometricClip? _clip;
		private CompositionRoundedRectangleGeometry? _clipGeometry;
		private SizeInt32 _lastFrameSize;
		private SizeInt32 _lastSurfaceSize;
		private volatile bool _disposed;
		private volatile bool _paused;
		// Flips once the first captured frame has actually been blitted into the surface.
		// Start() creates that surface blank, so anything drawing this session renders
		// fully transparent until then. A scene transition waits on this before parking
		// the real window the flying card is standing in for — otherwise the window goes
		// off-screen while its stand-in still has nothing to show.
		private volatile bool _hasFrame;

		// Cached so the pure perspective matrix on _rootContainer can be rebuilt
		// when either the container size or the depth changes.
		private Vector2 _hwndPixels;
		private float _perspectiveDepthPx; // 0 = no perspective until SetPerspective

		public Visual? RootVisual => _rootContainer;

		/// <summary>True once at least one captured frame has reached the surface.</summary>
		public bool HasFrame => _hasFrame;

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
			// Bounded — Start runs on the UI thread; an unbounded wait here stalls
			// the message pump if a capture-thread teardown is wedged.
			if (!sem.Wait(LockTimeout))
			{
				Log.Info("CAPSESS", $"Start timed out waiting on lock for 0x{_hwnd:X}");
				return;
			}
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

				// Root is the externally-visible camera: pure perspective + opacity.
				// The content visual below carries scale/shear/pull; sprite below that.
				_rootContainer = _compositor.CreateContainerVisual();
				_rootContainer.Size = Vector2.Zero;
				_contentVisual = _compositor.CreateContainerVisual();
				_contentVisual.Size = Vector2.Zero;
				_contentVisual.Children.InsertAtTop(_spriteVisual);
				_rootContainer.Children.InsertAtTop(_contentVisual);

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
				_hwndPixels = hwndPixels;
				if (_rootContainer is not null)
					_rootContainer.Size = hwndPixels;
				if (_contentVisual is not null)
					_contentVisual.Size = hwndPixels;
				if (_spriteVisual is not null)
				{
					_spriteVisual.Size = basePixels;
					// Center the base-sized sprite inside the oversized container.
					var off = (hwndPixels - basePixels) * 0.5f;
					_spriteVisual.Offset = new Vector3(off.X, off.Y, 0f);
					// Pivot for the native Y-rotation (perspective) — the sprite's own center.
					_spriteVisual.CenterPoint = new Vector3(basePixels.X * 0.5f, basePixels.Y * 0.5f, 0f);
				}
				if (_clipGeometry is not null)
					_clipGeometry.Size = basePixels;
				RebuildPerspective();
			}
		}

		// The affine content transform (scale/shear/cursor-pull) — set on the
		// content visual, NOT the root, so the root keeps a pure perspective matrix.
		public void SetTransformMatrix(System.Numerics.Matrix4x4 transform)
		{
			lock (_frameLock)
			{
				if (_disposed || _contentVisual is null) return;
				_contentVisual.TransformMatrix = transform;
			}
		}

		/// <summary>
		/// Constant native 3D rotation of the inner sprite about its vertical axis.
		/// Combined with the perspective divide in the container's TransformMatrix
		/// this foreshortens the card into the resting trapezoid — perspective on
		/// the parent container, rotation on the child sprite, per the documented
		/// Composition perspective pattern (a perspective matrix only foreshortens
		/// a CHILD visual's 3D rotation, never its own).
		/// </summary>
		public void SetSpriteRotationY(float degrees)
		{
			lock (_frameLock)
			{
				if (_disposed || _spriteVisual is null) return;
				_spriteVisual.RotationAxis = new Vector3(0f, 1f, 0f);
				_spriteVisual.RotationAngleInDegrees = degrees;
			}
		}

		/// <summary>
		/// Sets the perspective vanishing-distance (px) for the pure perspective
		/// matrix on _rootContainer that foreshortens the sprite's Y-rotation into
		/// the resting trapezoid. Smaller = stronger; 0 disables perspective.
		/// </summary>
		public void SetPerspective(float depthPx)
		{
			lock (_frameLock)
			{
				if (_disposed) return;
				_perspectiveDepthPx = depthPx;
				RebuildPerspective();
			}
		}

		// Builds T(-c)*P*T(c) (P.M34 = -1/depth) on _rootContainer, centered on the
		// current container size. Caller holds _frameLock.
		private void RebuildPerspective()
		{
			if (_rootContainer is null) return;
			if (_perspectiveDepthPx <= 0f || _hwndPixels.X <= 0f || _hwndPixels.Y <= 0f)
			{
				_rootContainer.TransformMatrix = Matrix4x4.Identity;
				return;
			}
			float cx = _hwndPixels.X * 0.5f;
			float cy = _hwndPixels.Y * 0.5f;
			var p = Matrix4x4.Identity;
			p.M34 = -1f / _perspectiveDepthPx;
			_rootContainer.TransformMatrix =
				Matrix4x4.CreateTranslation(-cx, -cy, 0f) * p * Matrix4x4.CreateTranslation(cx, cy, 0f);
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

		/// <summary>
		/// Stops frame delivery. Never blocks the caller: Pause is driven from the UI
		/// thread by IsVisibleChanged (including the WM_CLOSE cascade), and the per-hwnd
		/// lock may be held by a capture thread. A blocking wait here stalls the message
		/// pump — the observed "StageManager.exe is delaying system shutdown after
		/// 5016 ms". On contention the teardown is finished on the threadpool.
		/// </summary>
		public void Pause()
		{
			if (_paused || _disposed) return;
			var sem = _hwndLocks.GetOrAdd(_hwnd, _ => new SemaphoreSlim(1, 1));
			if (!sem.Wait(0))
			{
				ThreadPool.QueueUserWorkItem(_ => PauseDeferred(sem));
				return;
			}
			try { PauseLocked(); }
			finally { sem.Release(); }
		}

		private void PauseDeferred(SemaphoreSlim sem)
		{
			// Threadpool callback: an escaping exception kills the process. The
			// semaphore itself can be disposed out from under us by a concurrent
			// Dispose() (which removes it from _hwndLocks), so guard the wait too.
			bool held = false;
			try
			{
				held = sem.Wait(LockTimeout);
				if (!held) { Log.Info("CAPSESS", $"Pause timed out waiting on lock for 0x{_hwnd:X}"); return; }
				PauseLocked();
			}
			catch (Exception ex) { Log.Info("CAPSESS", $"Deferred pause failed for 0x{_hwnd:X}: {ex.Message}"); }
			finally { if (held) { try { sem.Release(); } catch { } } }
		}

		// Caller holds the per-hwnd semaphore.
		private void PauseLocked()
		{
			if (_disposed || _paused) return;
			// Set first: OnFrameArrived short-circuits on _paused, so frame work stops
			// even if the WinRT teardown below fails.
			_paused = true;
			ReleaseCaptureObjects("Pause");
			Log.Info("CAPSESS", $"Paused capture for 0x{_hwnd:X}");
		}

		/// <summary>
		/// Disposes the framepool + session. Every call is wrapped: WinRT teardown
		/// throws RPC_E_CANTCALLOUT_ININPUTSYNCCALL (0x8001010D) when it lands while
		/// the thread is dispatching an input-synchronous message (WM_CLOSE), and the
		/// process must not die there — dying mid-teardown is exactly what leaks the
		/// remaining sessions and leaves DWM capturing with no consumer.
		/// </summary>
		private void ReleaseCaptureObjects(string origin)
		{
			if (_session is not null)
			{
				try { _session.Dispose(); }
				catch (Exception ex) { Log.Info("CAPSESS", $"{origin}: session dispose threw for 0x{_hwnd:X}: {ex.Message}"); }
				_session = null;
			}
			if (_framePool is not null)
			{
				try
				{
					_framePool.FrameArrived -= OnFrameArrived;
					_framePool.Dispose();
				}
				catch (Exception ex) { Log.Info("CAPSESS", $"{origin}: framepool dispose threw for 0x{_hwnd:X}: {ex.Message}"); }
				_framePool = null;
			}
		}

		public void Resume()
		{
			if (!_paused || _disposed) return;
			var sem = _hwndLocks.GetOrAdd(_hwnd, _ => new SemaphoreSlim(1, 1));
			// Same no-block rule as Pause — Resume also runs on the UI thread.
			if (!sem.Wait(0))
			{
				ThreadPool.QueueUserWorkItem(_ => ResumeDeferred(sem));
				return;
			}
			try { ResumeLocked(); }
			finally { sem.Release(); }
		}

		private void ResumeDeferred(SemaphoreSlim sem)
		{
			// See PauseDeferred — threadpool callback, must swallow everything.
			bool held = false;
			try
			{
				held = sem.Wait(LockTimeout);
				if (!held) { Log.Info("CAPSESS", $"Resume timed out waiting on lock for 0x{_hwnd:X}"); return; }
				ResumeLocked();
			}
			catch (Exception ex) { Log.Info("CAPSESS", $"Deferred resume failed for 0x{_hwnd:X}: {ex.Message}"); }
			finally { if (held) { try { sem.Release(); } catch { } } }
		}

		private static void DisposeQuietly(IDisposable? d)
		{
			if (d is null) return;
			try { d.Dispose(); }
			catch (Exception ex) { Log.Info("CAPSESS", $"Visual dispose threw: {ex.Message}"); }
		}

		// Caller holds the per-hwnd semaphore.
		private void ResumeLocked()
		{
			if (_disposed || _item is null || !_paused) return;
			// A deferred Pause may have raced us; make sure nothing is left running
			// before building a second framepool on the same item.
			ReleaseCaptureObjects("Resume");
			try { BuildPool(); }
			catch (Exception ex)
			{
				Log.Info("CAPSESS", $"Resume: BuildPool threw for 0x{_hwnd:X}: {ex.Message}");
				return;
			}
			_paused = false;
			Log.Info("CAPSESS", $"Resumed capture for 0x{_hwnd:X}");
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

					_hasFrame = true;
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
			// Bounded, and we proceed even on timeout: _disposed is already set and
			// _frameLock below still fences an in-flight blit, so the GPU teardown is
			// safe. Hanging here instead would deadlock shutdown.
			var held = sem is null || sem.Wait(LockTimeout);
			if (!held) Log.Info("CAPSESS", $"Dispose timed out waiting on lock for 0x{_hwnd:X}, proceeding");
			try
			{
				// Take _frameLock so an in-flight OnFrameArrived completes before
				// we dispose GPU resources. Unhook _item.Closed FIRST so disposing
				// the session can't synthesize a Closed callback that re-enters
				// Dispose.
				lock (_frameLock)
				{
					if (_item is not null)
					{
						try { _item.Closed -= OnItemClosed; }
						catch (Exception ex) { Log.Info("CAPSESS", $"Dispose: item unhook threw for 0x{_hwnd:X}: {ex.Message}"); }
					}
					// Wrapped — see ReleaseCaptureObjects: WinRT teardown throws
					// 0x8001010D under an input-synchronous message (WM_CLOSE).
					ReleaseCaptureObjects("Dispose");
					_item = null;
					// Dispose container LAST among visuals — children are
					// disposed by the parent container automatically, but we
					// null our refs first so any racing callback short-circuits.
					// Each is individually wrapped: one visual already torn down
					// under us must not abort the rest of the teardown.
					DisposeQuietly(_spriteVisual); _spriteVisual = null;
					DisposeQuietly(_surfaceBrush); _surfaceBrush = null;
					DisposeQuietly(_clip); _clip = null;
					DisposeQuietly(_clipGeometry); _clipGeometry = null;
					DisposeQuietly(_contentVisual); _contentVisual = null;
					DisposeQuietly(_rootContainer); _rootContainer = null;
					DisposeQuietly(_surface); _surface = null;
					_surfaceInterop = null;
				}
				Log.Info("CAPSESS", $"Disposed capture for 0x{_hwnd:X}");
			}
			finally
			{
				if (held) sem?.Release();
				// Drop the per-hwnd semaphore so the static dictionary doesn't
				// grow unbounded across short-lived windows. NOT disposed: a
				// deferred Pause/Resume may still be waiting on it, and
				// SemaphoreSlim.Dispose would hand them an ObjectDisposedException
				// on a threadpool thread. Nothing here uses AvailableWaitHandle,
				// so there is no unmanaged handle to reclaim.
				if (sem is not null) _hwndLocks.TryRemove(_hwnd, out _);
			}
		}
	}
}
