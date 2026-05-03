using System.Windows.Controls;
using StageManager.Controls;

namespace StageManager.Animations
{
	public partial class TransitionOverlayWindow : LayeredOverlayWindowBase
	{
		public TransitionOverlayWindow()
		{
			InitializeComponent();
		}

		public Canvas Canvas => AnimationCanvas;
	}
}
