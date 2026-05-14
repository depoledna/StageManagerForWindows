using System;
using System.Runtime.InteropServices;
using Windows.UI.Composition;

namespace StageManager.Composition.Interop
{
	/// <summary>
	/// Hand-declared COM interop for <c>Windows.UI.Composition</c> bridges that
	/// are not provided by the CsWinRT projection. These are the well-known
	/// ABI interfaces exposed by <c>Compositor</c> and
	/// <c>CompositionDrawingSurface</c>; we cast (QI) the projected WinRT
	/// objects to them with <c>compositor.As&lt;T&gt;()</c>.
	/// </summary>
	// All parameters are raw IntPtr (IInspectable*) — see the note in
	// CompositorFactory.cs. Letting the CLR marshal a WinRT projected type
	// (CompositionGraphicsDevice / ICompositionSurface) directly through a
	// COM ABI signature breaks under CsWinRT 2.x because the runtime
	// produces a __ComObject and fails to cast it to the projection.
	// We unwrap manually with MarshalInspectable<T>.FromAbi at the call site.
	[ComImport]
	[Guid("25297D5C-3AD4-4C9C-B5CF-E36A38512330")]
	[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
	internal interface ICompositorInterop
	{
		void CreateCompositionSurfaceForHandle(
			IntPtr swapChain,
			out IntPtr result);

		void CreateCompositionSurfaceForSwapChain(
			IntPtr swapChain,
			out IntPtr result);

		void CreateGraphicsDevice(
			IntPtr renderingDevice,
			out IntPtr result);
	}

	/// <summary>
	/// ABI interface exposed by every <c>CompositionDrawingSurface</c>; gives
	/// access to the underlying texture for drawing without round-tripping
	/// through CPU memory.
	/// </summary>
	[ComImport]
	[Guid("FD04E6E3-FE0C-4C3C-AB19-A07601A576EE")]
	[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
	internal interface ICompositionDrawingSurfaceInterop
	{
		void BeginDraw(
			IntPtr updateRect,
			[In] ref Guid iid,
			out IntPtr updateObject,
			out System.Drawing.Point updateOffset);

		void EndDraw();

		void Resize(System.Drawing.Size sizePixels);
	}

	/// <summary>
	/// Bridge from a WinRT <c>IDirect3DSurface</c> (returned by
	/// <c>Direct3D11CaptureFrame.Surface</c>) to the underlying D3D11 / DXGI
	/// COM object. Pass the GUID of <c>ID3D11Texture2D</c> or
	/// <c>IDXGISurface</c>; receive a raw <c>IUnknown*</c> the caller must
	/// release.
	/// </summary>
	[ComImport]
	[Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1")]
	[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
	internal interface IDirect3DDxgiInterfaceAccess
	{
		IntPtr GetInterface([In] ref Guid iid);
	}
}
