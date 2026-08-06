using System;
using System.Collections.Generic;
using System.Numerics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using StageManager.Composition;

namespace StageManager.Controls
{
	/// <summary>
	/// Window thumbnail control backed by
	/// Windows.Graphics.Capture + Windows.UI.Composition. Public surface
	/// is identical: a single <see cref="PreviewHandle"/> dependency
	/// property; layout (Width/Height/Margin) is honoured by WPF.
	/// </summary>
	public partial class CompositionThumbnail : UserControl
	{
		/// <summary>
		/// Resting vertical-shear skew of tray thumbnails, in degrees. Single
		/// source of truth: bound by XAML (x:Static) and reused by the scene-
		/// switch animator so the flying placeholder matches the tray skew.
		/// </summary>
		public const double TrayTiltDegrees = 2.0;

		/// <summary>
		/// Vanishing distance (physical px) of the perspective divide (M34 = -1/depth)
		/// that CaptureSession puts on the tile's root container. It foreshortens the
		/// inner sprite's native vertical-axis rotation into the resting trapezoid.
		/// Smaller = stronger. The rotation itself is no longer a constant: it is
		/// solved per tile from the requested edge angles, see ApplySpriteRotation.
		/// </summary>
		public const double PerspectiveDepthPx = 220.0;

		private CompositionHost? _compositionHost;
		private D3DDeviceHolder? _devices;
		private CaptureSession? _session;
		private EventHandler? _deviceLostHandler;
		// Base = the visible "at rest" rect (matches WPF Width/Height).
		// Hwnd = the oversized HWND that gives MirrorScale headroom so the
		// rounded clip on the inner SpriteVisual never escapes the HWND rect.
		private double _lastBasePixelWidth;
		private double _lastBasePixelHeight;
		private double _lastHwndPixelWidth;
		private double _lastHwndPixelHeight;
		private double _lastAppliedRadiusPx = double.NaN;
		private Matrix4x4 _lastAppliedTransform = Matrix4x4.Identity;
		private double _lastAppliedRotationDegrees = double.NaN;
		private float _lastAppliedOpacity = 1f;
		// While true the live capture visual is on loan to the sidebar drag
		// ghost, which drives the shared session's size/transform directly.
		// The tile must stop touching the session or the two fight over
		// _rootContainer's TransformMatrix every time a bound DP ticks.
		private bool _borrowed;

		// Teardown requested while the visual was lent out (e.g. the tile
		// unloaded because its scene left the tray mid-flight). Disposing the
		// session then would yank live visuals out from under the flying card
		// (WinRT E_INVALIDARG on every later touch), so defer until return.
		private bool _teardownPending;

		// Each side. Total HWND inflation = 1 + 2 * HoverHeadroom. 30% per side
		// covers worst-case lateral overflow at peak hover — the pop is left-anchored,
		// so all 8% of it lands on the right — plus the 3D-tilt near-edge enlargement. Shared: LiveCardHost sizes the
		// borrowed drag/fly card with the same headroom so the host rect matches
		// the tile's and the skewed edge isn't clipped differently at handoff.
		internal const double HoverHeadroom = 0.30;

		// Every live tile, so shutdown can dispose their sessions up-front instead of
		// discovering them one WM_CLOSE visibility flip at a time. UI-thread only.
		private static readonly List<CompositionThumbnail> s_live = new();

		// Set by ShutdownAll before the window teardown cascade starts. Once true,
		// no tile may touch WinRT again: the cascade runs inside an input-synchronous
		// WM_CLOSE, where any outgoing cross-apartment call fails with
		// RPC_E_CANTCALLOUT_ININPUTSYNCCALL (0x8001010D).
		private static bool s_shuttingDown;

		public CompositionThumbnail()
		{
			InitializeComponent();
			Loaded += OnLoaded;
			Unloaded += OnUnloaded;
			IsVisibleChanged += OnIsVisibleChanged;
			SizeChanged += OnSizeChanged;
			s_live.Add(this);
		}

		/// <summary>
		/// Disposes every live capture session while ordinary COM rules still apply —
		/// call from Window.OnClosing, BEFORE the WM_CLOSE visual-tree cascade. Leaving
		/// it to the cascade throws 0x8001010D, kills the process mid-teardown, and
		/// strands the surviving GraphicsCaptureSessions: DWM keeps capturing those
		/// windows at full refresh rate with no consumer, dragging the whole desktop.
		/// </summary>
		internal static void ShutdownAll()
		{
			if (s_shuttingDown) return;
			s_shuttingDown = true;

			// Snapshot — TeardownSession can unregister as it goes.
			var live = s_live.ToArray();
			s_live.Clear();
			Log.Info("COMPTHUMB", $"Shutdown: disposing {live.Length} capture session(s)");

			foreach (var tile in live)
			{
				// A borrowed tile would normally defer teardown; there is no borrower
				// left to return the visual, so force it.
				tile._borrowed = false;
				try { tile.TeardownSession(); }
				catch (Exception ex) { Log.Info("COMPTHUMB", $"Shutdown teardown threw: {ex.Message}"); }
			}
		}

		public static readonly DependencyProperty PreviewHandleProperty = DependencyProperty.Register(
			nameof(PreviewHandle),
			typeof(IntPtr),
			typeof(CompositionThumbnail),
			new PropertyMetadata(IntPtr.Zero));

		public IntPtr PreviewHandle
		{
			get { return (IntPtr)GetValue(PreviewHandleProperty); }
			set { SetValue(PreviewHandleProperty, value); }
		}

		public static readonly DependencyProperty CornerRadiusProperty = DependencyProperty.Register(
			nameof(CornerRadius),
			typeof(double),
			typeof(CompositionThumbnail),
			new PropertyMetadata(0.0, OnCornerRadiusChanged));

		public double CornerRadius
		{
			get => (double)GetValue(CornerRadiusProperty);
			set => SetValue(CornerRadiusProperty, value);
		}

		private static void OnCornerRadiusChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
		{
			if (d is CompositionThumbnail ct)
				ct.ApplyCornerRadius();
		}

		/// <summary>
		/// Angle of the tile's TOP edge in degrees, POSITIVE = its right end rises,
		/// 0 = horizontal. Set per row from MainWindow's hardcoded tray table.
		/// Together with <see cref="BottomEdgeDegrees"/> it pins both edges of the
		/// resting trapezoid exactly; the left/right sides stay vertical.
		/// </summary>
		public static readonly DependencyProperty TopEdgeDegreesProperty = DependencyProperty.Register(
			nameof(TopEdgeDegrees),
			typeof(double),
			typeof(CompositionThumbnail),
			new PropertyMetadata(0.0, OnTransformInputChanged));

		public double TopEdgeDegrees
		{
			get => (double)GetValue(TopEdgeDegreesProperty);
			set => SetValue(TopEdgeDegreesProperty, value);
		}

		/// <summary>
		/// Angle of the tile's BOTTOM edge in degrees, POSITIVE = its right end rises.
		/// Bottom above top (bottom &gt; top) means the two edges converge to the
		/// right — the receding-card look. Equal values = parallel edges, i.e. a pure
		/// shear with no perspective at all.
		/// </summary>
		public static readonly DependencyProperty BottomEdgeDegreesProperty = DependencyProperty.Register(
			nameof(BottomEdgeDegrees),
			typeof(double),
			typeof(CompositionThumbnail),
			new PropertyMetadata(0.0, OnTransformInputChanged));

		public double BottomEdgeDegrees
		{
			get => (double)GetValue(BottomEdgeDegreesProperty);
			set => SetValue(BottomEdgeDegreesProperty, value);
		}

		// Mirrors the WPF ancestor's animated ScaleX/Y onto the SpriteVisual
		// because HwndHost child windows ignore WPF RenderTransform.
		public static readonly DependencyProperty MirrorScaleProperty = DependencyProperty.Register(
			nameof(MirrorScale),
			typeof(double),
			typeof(CompositionThumbnail),
			new PropertyMetadata(1.0, OnTransformInputChanged));

		public double MirrorScale
		{
			get => (double)GetValue(MirrorScaleProperty);
			set => SetValue(MirrorScaleProperty, value);
		}

		// Mirrors the WPF ancestor's animated Opacity onto the SpriteVisual.
		public static readonly DependencyProperty MirrorOpacityProperty = DependencyProperty.Register(
			nameof(MirrorOpacity),
			typeof(double),
			typeof(CompositionThumbnail),
			new PropertyMetadata(1.0, OnMirrorOpacityChanged));

		public double MirrorOpacity
		{
			get => (double)GetValue(MirrorOpacityProperty);
			set => SetValue(MirrorOpacityProperty, value);
		}

		private static void OnTransformInputChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
		{
			if (d is CompositionThumbnail ct)
				ct.ApplyTransform();
		}

		private static void OnMirrorOpacityChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
		{
			if (d is CompositionThumbnail ct)
				ct.ApplyOpacity();
		}

		protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
		{
			base.OnPropertyChanged(e);

			if (e.Property == PreviewHandleProperty)
			{
				if ((IntPtr)e.OldValue == IntPtr.Zero && (IntPtr)e.NewValue != IntPtr.Zero)
					StartCaptureIfReady();
				else if ((IntPtr)e.NewValue == IntPtr.Zero && _session is not null)
					TeardownSession();
			}
		}

		// IsVisible isn't a regular DP — changes don't always route through
		// OnPropertyChanged. Subscribe to the dedicated event.
		private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
		{
			// Closing the window makes WPF flip IsVisible on the whole visual tree
			// from inside WM_CLOSE. Sessions are already gone (ShutdownAll ran in
			// OnClosing) and any WinRT call from here would throw 0x8001010D.
			if (s_shuttingDown) return;

			// While lent to a fly animation the borrower owns the session's
			// run state; ignore tile-visibility flips (the switch sets the tile
			// invisible mid-flight and would otherwise pause the live frame).
			if (_borrowed) return;
			var nowVisible = (bool)e.NewValue;
			if (!nowVisible)
				_session?.Pause();
			else if (_session is not null)
				_session.Resume();
			else if (PreviewHandle != IntPtr.Zero)
				StartCaptureIfReady();
		}

		private void OnLoaded(object sender, RoutedEventArgs e)
		{
			if (_compositionHost is not null) return;

			_compositionHost = new CompositionHost();
			HostContainer.Children.Add(_compositionHost);

			var compositor = _compositionHost.Compositor;
			_devices = D3DDeviceHolder.GetOrCreate(compositor);
			_deviceLostHandler = OnDeviceLost;
			_devices.DeviceLost += _deviceLostHandler;

			if (PreviewHandle != IntPtr.Zero && IsVisible)
				StartCaptureIfReady();
		}

		private void OnUnloaded(object sender, RoutedEventArgs e)
		{
			s_live.Remove(this);

			// ShutdownAll already disposed every session; the rest of this teardown
			// is WinRT interop that is illegal inside the WM_CLOSE cascade.
			if (s_shuttingDown) return;

			TeardownSession();

			if (_devices is not null && _deviceLostHandler is not null)
				_devices.DeviceLost -= _deviceLostHandler;
			_deviceLostHandler = null;
			_devices = null;

			if (_compositionHost is not null)
			{
				try { HostContainer.Children.Remove(_compositionHost); }
				catch (Exception ex) { Log.Info("COMPTHUMB", $"HostContainer.Remove threw: {ex.Message}"); }
				try { _compositionHost.Dispose(); }
				catch (Exception ex) { Log.Info("COMPTHUMB", $"CompositionHost.Dispose threw: {ex.Message}"); }
				_compositionHost = null;
			}
		}

		private void StartCaptureIfReady()
		{
			// Never resurrect a session once shutdown has begun — covers the
			// TargetClosed / DeviceLost dispatcher callbacks that can land late.
			if (s_shuttingDown) return;
			if (_compositionHost is null || _devices is null) return;
			if (_session is not null) return;
			if (PreviewHandle == IntPtr.Zero) return;

			_session = new CaptureSession(PreviewHandle, _compositionHost.Compositor, _devices);
			_session.TargetClosed += OnTargetClosed;
			_session.Start();
			_compositionHost.Root = _session.RootVisual;

			RecomputePixelSize();
			ApplyCornerRadius();
			ApplyTransform();
			ApplyOpacity();

			Log.Info("COMPTHUMB", $"Started session for 0x{PreviewHandle:X}");
		}

		private void OnSizeChanged(object sender, SizeChangedEventArgs e)
		{
			RecomputePixelSize();
			ApplyCornerRadius();
			ApplyTransform();
		}

		protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
		{
			base.OnDpiChanged(oldDpi, newDpi);
			RecomputePixelSize();
			ApplyCornerRadius();
			ApplyTransform();
		}

		private void RecomputePixelSize()
		{
			if (_session is null || _borrowed) return;
			var dpi = VisualTreeHelper.GetDpi(this);
			var baseW = ActualWidth * dpi.DpiScaleX;
			var baseH = ActualHeight * dpi.DpiScaleY;
			var hwndW = baseW * (1.0 + 2.0 * HoverHeadroom);
			var hwndH = baseH * (1.0 + 2.0 * HoverHeadroom);

			// Drive HostContainer's WPF layout size so the underlying HwndHost
			// reserves the oversized HWND. HorizontalAlignment=Center +
			// explicit Width/Height in XAML cause WPF to position it centered
			// inside the UserControl, overflowing equally on each side.
			HostContainer.Width = ActualWidth * (1.0 + 2.0 * HoverHeadroom);
			HostContainer.Height = ActualHeight * (1.0 + 2.0 * HoverHeadroom);

			if (baseW == _lastBasePixelWidth && baseH == _lastBasePixelHeight &&
				hwndW == _lastHwndPixelWidth && hwndH == _lastHwndPixelHeight) return;

			_lastBasePixelWidth = baseW;
			_lastBasePixelHeight = baseH;
			_lastHwndPixelWidth = hwndW;
			_lastHwndPixelHeight = hwndH;
			// Size changed → cached radius / transform are tied to the old size.
			_lastAppliedRadiusPx = double.NaN;
			_lastAppliedTransform = new Matrix4x4(float.NaN, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
			// Base height feeds the rotation solve, so a size change invalidates it too.
			_lastAppliedRotationDegrees = double.NaN;
			_session.SetVisualSize(
				new Vector2((float)hwndW, (float)hwndH),
				new Vector2((float)baseW, (float)baseH));
			_session.SetPerspective((float)PerspectiveDepthPx);
		}

		private void ApplyCornerRadius()
		{
			if (_session is null || _borrowed) return;
			var dpi = VisualTreeHelper.GetDpi(this);
			var pixelRadius = CornerRadius * dpi.DpiScaleX;
			if (pixelRadius == _lastAppliedRadiusPx) return;
			_lastAppliedRadiusPx = pixelRadius;
			// Clip lives on the inner sprite (base pixel size), not the
			// oversized HWND-sized container.
			_session.SetCornerRadius(
				(float)pixelRadius,
				new Vector2((float)_lastBasePixelWidth, (float)_lastBasePixelHeight));
		}

		// Combined skew + scale transform around the visual center. Mirrors the
		// WPF ancestor's animated ScaleTransform — HwndHost children ignore
		// WPF RenderTransform, so the SpriteVisual.TransformMatrix has to do it.
		private void ApplyTransform()
		{
			if (_session is null || _borrowed) return;
			// Transform applied to the HWND-sized container — center is HWND/2,
			// which coincides with the inner sprite's center (sprite is
			// centered inside the container).

			// Shear + sprite rotation are solved together from the edge-angle pair and
			// the CURRENT base height — see SolveEdgeAngles for the derivation and for
			// why the height has to feed back in on every size change.
			var (shearDegrees, rotationDegrees) = SolveEdgeAngles(TopEdgeDegrees, BottomEdgeDegrees, _lastBasePixelHeight);

			if (rotationDegrees != _lastAppliedRotationDegrees)
			{
				_lastAppliedRotationDegrees = rotationDegrees;
				_session.SetSpriteRotationY((float)rotationDegrees);
			}

			var matrix = ComposeTransform(shearDegrees, MirrorScale, _lastBasePixelWidth, _lastHwndPixelWidth, _lastHwndPixelHeight);
			if (matrix == _lastAppliedTransform) return;
			_lastAppliedTransform = matrix;
			_session.SetTransformMatrix(matrix);
		}

		/// <summary>
		/// Splits a pair of requested edge angles into the two knobs that reproduce
		/// them: the affine vertical shear carried on the content visual, and the
		/// sprite's native Y-rotation that the root's perspective divide foreshortens
		/// into the converging pair.
		/// <para>
		/// Screen slope (y grows DOWN) of an edge is -tan(deg), because the DPs use
		/// "positive = right end rises". Pushing the rect through sprite-rotation →
		/// content-shear → perspective-divide gives, for shear slope T (M12) and
		/// convergence Q = H*tan(theta)/(2*depth): slope(top) = T + Q,
		/// slope(bottom) = T - Q. Width and hover scale both cancel out, so the split is
		/// exact and stays exact while the tile is popped.
		/// </para>
		/// <para>
		/// <b>Must be re-solved whenever the sprite's pixel HEIGHT changes.</b> Inverting
		/// Q for theta is what makes the look size- and DPI-independent; a rotation held
		/// fixed while the sprite grows produces convergence proportional to H, and the
		/// perspective divide (fixed <see cref="PerspectiveDepthPx"/> vanishing distance)
		/// then blows up with half-width. A tray-sized solve reused on a stage-sized
		/// flying card sends one vertical edge past 1.8x — taller than the screen.
		/// </para>
		/// </summary>
		internal static (double ShearDegrees, double SpriteRotationDegrees) SolveEdgeAngles(
			double topEdgeDegrees, double bottomEdgeDegrees, double baseHeightPx)
		{
			var slopeTop = -Math.Tan(topEdgeDegrees * Math.PI / 180.0);
			var slopeBottom = -Math.Tan(bottomEdgeDegrees * Math.PI / 180.0);
			var shearSlope = (slopeTop + slopeBottom) / 2.0;
			var converge = (slopeTop - slopeBottom) / 2.0;

			var shearDegrees = Math.Atan(shearSlope) * 180.0 / Math.PI;
			// converge == 0 solves to 0 degrees, i.e. parallel edges, no divide.
			var rotationDegrees = baseHeightPx > 0.0
				? Math.Atan(2.0 * PerspectiveDepthPx * converge / baseHeightPx) * 180.0 / Math.PI
				: 0.0;

			return (shearDegrees, rotationDegrees);
		}

		private void ApplyOpacity()
		{
			if (_session is null || _borrowed) return;
			var op = (float)Math.Clamp(MirrorOpacity, 0.0, 1.0);
			if (op == _lastAppliedOpacity) return;
			_lastAppliedOpacity = op;
			_session.SetOpacity(op);
		}

		/// <summary>
		/// Affine part of a tile's look: the per-row shear plus the hover scale, the latter
		/// anchored on the sprite's LEFT edge.
		/// <para>
		/// The anchor is why <paramref name="basePixelWidth"/> is here. The matrix has to
		/// pivot on the container centre — the shear's zero-crossing must stay there or the
		/// whole card slides vertically by tan(shear)*halfWidth — so the scale is centred
		/// too and pulls the sprite's left edge out by (s-1)*baseW/2. The tray aligns every
		/// tile on one left edge, so that bias is re-added as a translation and the growth
		/// ends up all on the right. Matches RenderTransformOrigin="0,0.5" on the WPF tile.
		/// </para>
		/// </summary>
		internal static Matrix4x4 ComposeTransform(double angleDegrees, double scale, double basePixelWidth, double pixelWidth, double pixelHeight)
		{
			var s = (float)scale;
			var tx = (float)((scale - 1.0) * basePixelWidth / 2.0);

			// No identity early-out: the perspective below is applied to EVERY tile,
			// so even a flat middle row (angle 0, scale 1, no pull) gets the trapezoid.
			var cx = (float)(pixelWidth / 2.0);
			var cy = (float)(pixelHeight / 2.0);

			var inner = Matrix4x4.CreateScale(s, s, 1f);
			if (angleDegrees != 0.0)
			{
				// 2D vertical shear, the component both edges share: y' = y + tan(t)*x.
				// Sides stay vertical; +angle drops the right edge, -angle raises it
				// (SCREEN convention, opposite of the Top/BottomEdgeDegrees DPs).
				// Tiles pass the mean of their two edge slopes; the difference between
				// the edges is carried by the sprite rotation, not by this matrix.
				var rad = (float)(angleDegrees * Math.PI / 180.0);
				var shear = Matrix4x4.Identity;
				shear.M12 = (float)Math.Tan(rad);
				inner = inner * shear;
			}

			// Perspective and Y-rotation are NOT in this matrix — they live on
			// separate visuals in CaptureSession (pure perspective on the root,
			// native rotation on the sprite; see SetPerspective / SetSpriteRotationY),
			// because Composition only foreshortens when perspective sits alone on an
			// ancestor of the rotated visual. This matrix is affine only: scale +
			// per-row shear.

			// Center → transform → re-center, then bias re-center by the left-anchor shift.
			var t1 = Matrix4x4.CreateTranslation(-cx, -cy, 0);
			var t2 = Matrix4x4.CreateTranslation(cx + tx, cy, 0);
			return t1 * inner * t2;
		}

		/// <summary>
		/// The live capture session backing this tile, or null before it starts.
		/// Exposed so the sidebar drag ghost can drive size/skew on the shared
		/// visual while it is borrowed.
		/// </summary>
		internal CaptureSession? Session => _session;

		/// <summary>
		/// Lend the live composition visual to the drag ghost: detach it from this
		/// tile's host and stop driving the session. Returns null (caller falls back
		/// to a static placeholder) if no session is running yet.
		/// </summary>
		internal Windows.UI.Composition.Visual? BorrowRootVisual()
		{
			if (_session is null || _compositionHost is null) return null;
			var visual = _session.RootVisual;
			if (visual is null) return null;
			_borrowed = true;
			// Keep frames flowing even if the tile was paused (hidden) — the
			// flying card must be live, not a frozen last frame.
			_session.Resume();
			_compositionHost.Root = null;
			return visual;
		}

		/// <summary>
		/// Reclaim the visual after the drag ends and re-apply this tile's own
		/// size / corner / transform / opacity (the ghost left the shared session
		/// sized and skewed for the drag, so force past the value-cache guards).
		/// </summary>
		internal void ReturnRootVisual()
		{
			_borrowed = false;
			if (_teardownPending)
			{
				// Tile went away (or its window closed) while the visual was
				// lent out — finish the deferred teardown now instead of
				// reattaching. Also covers the unload-while-borrowed leak where
				// the session would otherwise never be disposed.
				TeardownSession();
				// If the tile is back in the tray by now, restart clean.
				if (IsLoaded && IsVisible && PreviewHandle != IntPtr.Zero)
					StartCaptureIfReady();
				return;
			}
			if (_compositionHost is null || _session is null) return;
			_compositionHost.Root = _session.RootVisual;
			_lastBasePixelWidth = 0;
			_lastBasePixelHeight = 0;
			_lastHwndPixelWidth = 0;
			_lastHwndPixelHeight = 0;
			_lastAppliedRadiusPx = double.NaN;
			_lastAppliedTransform = new Matrix4x4(float.NaN, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
			_lastAppliedRotationDegrees = double.NaN;
			_lastAppliedOpacity = float.NaN;
			RecomputePixelSize();
			ApplyCornerRadius();
			ApplyTransform();
			ApplyOpacity();
			// Match the tile's own visibility: if it's hidden in the tray, the
			// session should idle again now that the borrower is done.
			if (!IsVisible) _session.Pause();
		}

		private void TeardownSession()
		{
			if (_session is null) return;
			if (_borrowed)
			{
				_teardownPending = true;
				Log.Info("COMPTHUMB", $"Teardown deferred (visual borrowed) for 0x{PreviewHandle:X}");
				return;
			}
			_teardownPending = false;
			_session.TargetClosed -= OnTargetClosed;

			if (_compositionHost is not null)
				_compositionHost.Root = null;

			try { _session.Dispose(); }
			catch (Exception ex) { Log.Info("COMPTHUMB", $"Session.Dispose threw: {ex.Message}"); }
			_session = null;
			_lastBasePixelWidth = 0;
			_lastBasePixelHeight = 0;
			_lastHwndPixelWidth = 0;
			_lastHwndPixelHeight = 0;
			_lastAppliedRadiusPx = double.NaN;
			_lastAppliedTransform = new Matrix4x4(float.NaN, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
			_lastAppliedRotationDegrees = double.NaN;
			_lastAppliedOpacity = float.NaN;
		}

		private void OnTargetClosed(object? sender, EventArgs e)
		{
			// TargetClosed fires off-thread; marshal teardown to UI to keep
			// CompositionHost mutations on the dispatcher that owns it.
			Dispatcher.BeginInvoke(new Action(() =>
			{
				if (_compositionHost is not null)
					_compositionHost.Root = null;
				TeardownSession();
			}));
		}

		private void OnDeviceLost(object? sender, EventArgs e)
		{
			Dispatcher.BeginInvoke(new Action(() =>
			{
				if (PreviewHandle == IntPtr.Zero) return;
				Log.Info("COMPTHUMB", $"Device lost — restarting session for 0x{PreviewHandle:X}");
				TeardownSession();
				if (IsVisible)
					StartCaptureIfReady();
			}));
		}
	}
}
