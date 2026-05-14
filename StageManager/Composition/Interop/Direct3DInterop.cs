using System;
using System.Runtime.InteropServices;
using Vortice.Direct3D11;
using Vortice.DXGI;
using WinRT;
using WinRTDirect3D11 = Windows.Graphics.DirectX.Direct3D11;

namespace StageManager.Composition.Interop
{
	/// <summary>
	/// Bridges Vortice's D3D11/DXGI COM wrappers to the WinRT
	/// <c>IDirect3DDevice</c> / <c>IDirect3DSurface</c> projections required
	/// by <c>Direct3D11CaptureFramePool</c>. All transfers stay GPU-side; no
	/// CPU readback occurs.
	/// </summary>
	internal static class Direct3DInterop
	{
		private static readonly Guid s_iidTexture2D = typeof(ID3D11Texture2D).GUID;

		// HRESULT CreateDirect3D11DeviceFromDXGIDevice(IDXGIDevice*, IInspectable**)
		// PreserveSig = false → throws on non-zero HRESULT.
		[DllImport("d3d11.dll", PreserveSig = false)]
		private static extern void CreateDirect3D11DeviceFromDXGIDevice(
			IntPtr dxgiDevice,
			out IntPtr graphicsDevice);

		/// <summary>
		/// Wraps a Vortice <see cref="IDXGIDevice"/> as the WinRT
		/// <see cref="WinRTDirect3D11.IDirect3DDevice"/> consumed by
		/// <c>Direct3D11CaptureFramePool.Create</c>.
		/// </summary>
		public static WinRTDirect3D11.IDirect3DDevice CreateDirect3DDevice(IDXGIDevice dxgiDevice)
		{
			if (dxgiDevice is null)
				throw new ArgumentNullException(nameof(dxgiDevice));

			CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice.NativePointer, out IntPtr inspectable);
			try
			{
				// FromAbi wraps without consuming a ref; DisposeAbi (Release) below
				// balances the AddRef that CreateDirect3D11DeviceFromDXGIDevice gave us.
				return MarshalInspectable<WinRTDirect3D11.IDirect3DDevice>.FromAbi(inspectable);
			}
			finally
			{
				MarshalInspectable<WinRTDirect3D11.IDirect3DDevice>.DisposeAbi(inspectable);
			}
		}

		/// <summary>
		/// Extracts the underlying <see cref="ID3D11Texture2D"/> from a
		/// captured WinRT <see cref="WinRTDirect3D11.IDirect3DSurface"/>. The
		/// returned texture wraps the same GPU resource the capture session
		/// produced.
		/// </summary>
		public static ID3D11Texture2D GetTexture2DFromSurface(WinRTDirect3D11.IDirect3DSurface surface)
		{
			if (surface is null)
				throw new ArgumentNullException(nameof(surface));

			// CsWinRT 2.x's projection-aware path will not produce a
			// [ComImport] interface via direct cast — it reports an invalid
			// cast on the IInspectable RCW. Go through CastExtensions.As<>(),
			// which performs an explicit IUnknown QI for the [Guid] attribute.
			var access = surface.As<IDirect3DDxgiInterfaceAccess>();
			var iid = s_iidTexture2D;
			// GetInterface returns an AddRef'd IUnknown* (QI semantics).
			// Vortice's (IntPtr) ctor wraps without taking an extra ref, so
			// the AddRef from GetInterface becomes the wrapper's owning ref —
			// it is released when the caller disposes the texture.
			IntPtr pTex = access.GetInterface(ref iid);
			return new ID3D11Texture2D(pTex);
		}
	}
}
