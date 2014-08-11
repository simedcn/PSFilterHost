﻿/////////////////////////////////////////////////////////////////////////////////
//
// 8bf filter host for .NET
// http://psfilterhost.codeplex.com/
//
// This software is provided under the Microsoft Public License:
//   Copyright (C) 2012-2014 Nicholas Hayes
// 
// See LICENSE.txt for complete licensing and attribution information.
//
/////////////////////////////////////////////////////////////////////////////////

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using HostTest.Properties;
using HostTest.Tools;
using PSFilterHostDll;

namespace HostTest
{
	internal partial class Form1 : Form
	{
		private BitmapSource srcImage;
		private BitmapSource dstImage;
		private PseudoResourceCollection pseudoResources;
		private Dictionary<PluginData, ParameterData> paramDict;
		private HistoryStack historyStack;   
		private Thread filterThread;
		private bool setRepeatEffect;
		private bool setFilterApplyText;
		private string filterName;
		private string titleString;
		private string imageFileName;
		private string imageType;
		private Size panelClientSize;
		private AbortMessageFilter messageFilter;  
		private string srcImageTempFileName;
		private string currentPluginDirectory;
		private HostInformation hostInfo;
		private BitmapMetadata srcMetaData;
	 
		private static readonly System.Collections.ObjectModel.ReadOnlyCollection<string> ImageFileExtensions = WICHelpers.GetDecoderFileExtensions();
		
		public Form1()
		{
			InitializeComponent();
			this.srcImage = null;
			this.dstImage = null;
			this.pseudoResources = null;
			this.paramDict = new Dictionary<PluginData, ParameterData>();
			this.historyStack = new HistoryStack();            
			this.setRepeatEffect = false;          
			this.setFilterApplyText = false;
			this.filterName = string.Empty;
			this.imageFileName = string.Empty;
			this.imageType = string.Empty;
			this.panelClientSize = Size.Empty;
			this.srcImageTempFileName = string.Empty;
			this.currentPluginDirectory = string.Empty;
			this.hostInfo = new HostInformation();
			this.srcMetaData = null;

			if (IntPtr.Size == 8)
			{
				this.Text += " x64";
			}

			this.titleString = this.Text;
			
			this.messageFilter = new AbortMessageFilter();
			Application.AddMessageFilter(this.messageFilter);

			this.openFileDialog1.Filter = WICHelpers.GetOpenDialogFilterString();

			PaintDotNet.SystemLayer.UI.InitScaling(this);
			
			ScaleToolStripImageSize(this.menuStrip1);
			ScaleToolStripImageSize(this.toolStrip1);
		}

		/// <summary>
		/// Scales the size of the tool strip images to match the system DPI setting.
		/// </summary>
		/// <param name="toolStrip">The tool strip.</param>
		private static void ScaleToolStripImageSize(ToolStrip toolStrip)
		{
			Size scaledImageSize = PaintDotNet.SystemLayer.UI.ScaleSize(toolStrip.ImageScalingSize);

			if (toolStrip.ImageScalingSize != scaledImageSize)
			{
				// Temporarily disable the AutoSize property so the new ImageScalingSize will be used during layout, see http://msdn.microsoft.com/en-us/library/system.windows.forms.toolstrip.imagescalingsize.aspx.
				toolStrip.AutoSize = false;  
				toolStrip.ImageScalingSize = scaledImageSize;
				toolStrip.PerformLayout();
				toolStrip.AutoSize = true;
			}
		}

		private void ProcessCommandLine()
		{
			string[] args = Environment.GetCommandLineArgs();
			if (args.Length == 2)
			{			
				FileInfo info = new FileInfo(args[1]);

				if (info.Exists && ImageFileExtensions.Contains(info.Extension, StringComparer.OrdinalIgnoreCase))
				{
					try
					{
						OpenFile(args[1]);
					}
					catch (FileFormatException)
					{
					}
					catch (NotSupportedException) // WINCODEC_ERR_COMPONENTNOTFOUND
					{
					}
				}
			}
		}

		static class NativeMethods
		{
			[DllImport("kernel32.dll", EntryPoint = "SetProcessDEPPolicy")]
			[return: MarshalAs(UnmanagedType.Bool)]
			internal static extern bool SetProcessDEPPolicy(uint dwFlags);

			[DllImport("kernel32.dll", EntryPoint = "SetErrorMode")]
			internal static extern uint SetErrorMode(uint uMode);

			internal const uint SEM_FAILCRITICALERRORS = 1U;
			internal const uint SEM_NOOPENFILEERRORBOX = 0x8000U;
		}

		protected override void OnLoad(EventArgs e)
		{
			base.OnLoad(e);

			// Try to Opt-out of DEP when running as a 32-bit process as many filters do not support it.
			if (IntPtr.Size == 4)
			{
				try
				{
					NativeMethods.SetProcessDEPPolicy(0U);
				}
				catch (EntryPointNotFoundException)
				{
					// This method is only present on Vista SP1 or XP SP3 and later. 
				}
			}

			// Disable the error dialog that is shown when a filter cannot find a missing dependency.
			uint oldMode = NativeMethods.SetErrorMode(0U);
			NativeMethods.SetErrorMode(oldMode | NativeMethods.SEM_FAILCRITICALERRORS | NativeMethods.SEM_NOOPENFILEERRORBOX);
		}

		protected override void OnShown(EventArgs e)
		{
			base.OnShown(e);

			string pluginDir = Path.Combine(Application.StartupPath, "Plug-Ins");
			if (Directory.Exists(pluginDir))
			{
				QueryDirectory(pluginDir);
			}

			ProcessCommandLine();
		}

		private void ShowErrorMessage(string message)
		{
			if (base.InvokeRequired)
			{
				base.Invoke(new Action<string>(delegate(string error)
					{
						MessageBox.Show(this, error, this.Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
					}), new object[] { message });
			}
			else
			{
				MessageBox.Show(this, message, this.Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
			}
		}

		private void loadFiltersMenuItem_Click(object sender, EventArgs e)
		{
			this.folderBrowserDialog1.SelectedPath = string.Empty;

			if (folderBrowserDialog1.ShowDialog(this) == System.Windows.Forms.DialogResult.OK)
			{
				QueryDirectory(this.folderBrowserDialog1.SelectedPath);
			}
		}

		private void QueryDirectory(string path)
		{
			if (!backgroundWorker1.IsBusy)
			{
				this.Cursor = Cursors.WaitCursor;

				if (filtersToolStripMenuItem.HasDropDownItems)
				{
					this.filtersToolStripMenuItem.DropDownItems.Clear();
				}

				if (aboutPluginsMenuItem.HasDropDownItems)
				{
					this.aboutPluginsMenuItem.DropDownItems.Clear();
				}

				WorkerArgs args = new WorkerArgs(path);

				this.backgroundWorker1.RunWorkerAsync(args); 
			}
		}

		private void EnableFiltersForImageFormat()
		{
			if (srcImage != null)
			{
				PixelFormat format = srcImage.Format;

				ToolStripItemCollection items = filtersToolStripMenuItem.DropDownItems;
				for (int i = 0; i < items.Count; i++)
				{
					ToolStripMenuItemEx menu = items[i] as ToolStripMenuItemEx;

					if (menu != null)
					{
						var nodes = menu.SubMenuItems;
						int nCount = nodes.Count;
						List<bool> catEnabled = new List<bool>(nCount);

						for (int j = 0; j < nCount; j++)
						{
							PluginData data = (PluginData)nodes[j].Tag;

							bool enabled = data.SupportsImageMode(format);
							catEnabled.Add(enabled);
							nodes[j].Enabled = enabled;
						}

						menu.Enabled = catEnabled.Contains(true);
					}
					else
					{
						ToolStripItem repeatMenuItem = items[i];

						PluginData data = (PluginData)repeatMenuItem.Tag;

						repeatMenuItem.Enabled = data.SupportsImageMode(format);
					}
				}
					
			}
			else
			{
				ToolStripItemCollection items = filtersToolStripMenuItem.DropDownItems;
				for (int i = 0; i < items.Count; i++)
				{
					items[i].Enabled = false;
				}
			}
				
		}

		private void RunPhotoshopFilter_Click(object sender, EventArgs e)
		{
			ToolStripItem item = (ToolStripItem)sender;
			PluginData pluginData = (PluginData)item.Tag;
			this.setRepeatEffect = false;
			RunPhotoshopFilterThread(pluginData, false);

			if (setRepeatEffect)
			{
				SetRepeatEffectMenuItem(item);
				this.setRepeatEffect = false;
			}
		}

		private void ShowFilterAboutDialog(object sender, EventArgs e)
		{
			ToolStripItem item = (ToolStripItem)sender;
			PluginData pluginData = (PluginData)item.Tag;

			try
			{
				PSFilterHost.ShowAboutDialog(pluginData, this.Handle);
			}
			catch (FileNotFoundException ex)
			{
				ShowErrorMessage(ex.Message);
			}
			catch (FilterRunException ex)
			{
				ShowErrorMessage(ex.Message);
			}
		}

		private void RunPhotoshopFilterThread(PluginData pluginData, bool repeatEffect)
		{
			if (filterThread == null)
			{
				this.Cursor = Cursors.WaitCursor;

				this.filterThread = new Thread(() => RunPhotoshopFilterImpl(pluginData, repeatEffect)) { IsBackground = true, Priority = ThreadPriority.AboveNormal };
				this.filterThread.SetApartmentState(ApartmentState.STA); // Some filters may use OLE which requires Single Threaded Apartment mode.
				this.filterThread.Start();

				while (filterThread.IsAlive)
				{
					Application.DoEvents();
				}

				this.filterThread.Join();
				this.filterThread = null;
					
				this.Cursor = Cursors.Default;
				this.toolStripProgressBar1.Value = 0;
				this.toolStripProgressBar1.Visible = false;
				this.toolStripStatusLabel1.Text = string.Empty;
			}
		}

		private void SaveImageOnUIThread()
		{
			if (string.IsNullOrEmpty(srcImageTempFileName))
			{
				this.srcImageTempFileName = Path.GetTempFileName();

				BitmapMetadata metaData = null;

				try
				{
					metaData = this.srcImage.Metadata as BitmapMetadata;
				}
				catch (NotSupportedException)
				{
				}

				if (metaData != null)
				{
					BitmapMetadata convertedMetaData = MetaDataHelper.ConvertMetaDataToTIFF(metaData); // As WIC does not automatically convert between meta-data formats we have to do it manually.
					if (convertedMetaData != null)
					{
						this.srcMetaData = convertedMetaData.Clone();
						this.srcMetaData.Freeze();
					}
					metaData = convertedMetaData;
				}
				else
				{
					this.srcMetaData = null;
				}
					
				using (FileStream stream = new FileStream(srcImageTempFileName, FileMode.Create, FileAccess.Write))
				{
					TiffBitmapEncoder encoder = new TiffBitmapEncoder();
					encoder.Frames.Add(BitmapFrame.Create(this.srcImage, null, metaData, null));
					encoder.Save(stream);
				}  
			}
		}

		private static System.Windows.Media.Color GDIPlusToWPFColor(System.Drawing.Color color)
		{
			return System.Windows.Media.Color.FromArgb(color.A, color.R, color.G, color.B);
		}

		private void RunPhotoshopFilterImpl(PluginData pluginData, bool repeatEffect)
		{
			Region selection = null;

			if (canvas.ClipPath != null)
			{
				selection = new Region(canvas.ClipPath);
			}
			if (historyStack.Count == 0)
			{
				// add the original image to the history stack.
				base.BeginInvoke(new Action(delegate()
				{
					this.historyStack.AddHistoryItem(this.canvas.ToCanvasHistoryState(), this.srcImage);
				}));
			}

			BitmapSource image = null;

			if (dstImage == null)
			{
				// save the srcImage to a temporary file on the UI thread and load it on this thread to fix the cross threading issues.
				if (string.IsNullOrEmpty(srcImageTempFileName))
				{
					base.Invoke(new Action(SaveImageOnUIThread)); 
				}

				using (FileStream stream = new FileStream(srcImageTempFileName, FileMode.Open, FileAccess.Read))
				{
					image = BitmapFrame.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
				}

				hostInfo.Caption = MetaDataHelper.GetIPTCCaption(image);
			}
			else
			{
				image = BitmapFrame.Create(dstImage, null, srcMetaData, null); // Create a new BitmapFrame so the source image's meta-data is available to the filters.
			}

			IntPtr owner = (IntPtr)base.Invoke(new Func<IntPtr>(delegate() { return this.Handle; }));

			try
			{
				System.Windows.Media.Color primary = GDIPlusToWPFColor(this.primaryColorBtn.RectangleColor);
				System.Windows.Media.Color secondary = GDIPlusToWPFColor(this.secondaryColorBtn.RectangleColor);

				using (PSFilterHost host = new PSFilterHost(image, primary, secondary, selection, owner))
				{
					host.SetAbortCallback(new AbortFunc(this.messageFilter.AbortFilter));
					host.SetPickColorCallback(new PickColor(PickColor));
					host.UpdateProgress += new EventHandler<FilterProgressEventArgs>(UpdateFilterProgress);
					if (repeatEffect && paramDict.ContainsKey(pluginData))
					{
						host.FilterParameters = paramDict[pluginData];
					}

					if ((pseudoResources != null) && pseudoResources.Count > 0)
					{
						host.PseudoResources = pseudoResources;
					}

					host.HostInfo = this.hostInfo;

					this.filterName = pluginData.Title.TrimEnd('.');
					this.setFilterApplyText = false;
					this.messageFilter.Reset();

					if (host.RunFilter(pluginData))
					{
						this.dstImage = host.Dest;

						FormatConvertedBitmap convertedImage = null;

						int channelCount = dstImage.Format.Masks.Count;
						int bitsPerChannel = dstImage.Format.BitsPerPixel / channelCount;

						if (bitsPerChannel >= 16)
						{
							convertedImage = new FormatConvertedBitmap(this.dstImage, channelCount == 4 ? PixelFormats.Bgra32 : PixelFormats.Bgr24, null, 0.0);
						}

						this.canvas.SuspendPaint();
						using (MemoryStream stream = new MemoryStream())
						{
							PngBitmapEncoder enc = new PngBitmapEncoder();
							enc.Frames.Add(BitmapFrame.Create(convertedImage ?? this.dstImage));
							enc.Save(stream);

							base.Invoke(new Action<MemoryStream>(delegate(MemoryStream ms)
							{
								this.canvas.Surface = new Bitmap(ms, true);
							}), new object[] { stream });
						}

						this.historyStack.AddHistoryItem(this.canvas.ToCanvasHistoryState(), this.dstImage);

						this.canvas.ResumePaint();

						if (!repeatEffect)
						{
							if (paramDict.ContainsKey(pluginData))
							{
								paramDict[pluginData] = host.FilterParameters;
							}
							else
							{
								paramDict.Add(pluginData, host.FilterParameters);
							}

							this.pseudoResources = host.PseudoResources;
							this.hostInfo = host.HostInfo;
							this.setRepeatEffect = true;
						}

					}

				}
			}
			catch (FileNotFoundException ex)
			{
				ShowErrorMessage(ex.Message);
			}
			catch (FilterRunException ex)
			{
				string message = ex.Message;

				if (ex.InnerException != null)
				{
					message = ex.InnerException.ToString();
				}

				ShowErrorMessage(message);
			}
			catch (ImageSizeTooLargeException ex)
			{
				ShowErrorMessage(ex.Message);
			}
			finally
			{
				if (selection != null)
				{
					selection.Dispose();
					selection = null;
				}
			}

		}

		private void SetRepeatEffectMenuItem(ToolStripItem item)
		{
			if (!filtersToolStripMenuItem.DropDownItems.ContainsKey("repeatEffect"))
			{
				ToolStripMenuItem repeatItem = new ToolStripMenuItem(Resources.RepeatEffectMenuText + item.Text.TrimEnd('.'), null, new EventHandler(RepeatLastEffect))
				{
					Name = "repeatEffect",
					Tag = item.Tag,
					ShowShortcutKeys = true,
					ShortcutKeys = Keys.Control | Keys.F
				};

				filtersToolStripMenuItem.DropDownItems.Insert(0, repeatItem);
			}
			else
			{
				ToolStripMenuItem repeatItem = (ToolStripMenuItem)filtersToolStripMenuItem.DropDownItems[0];
				repeatItem.Text = Resources.RepeatEffectMenuText + item.Text.TrimEnd('.');
				repeatItem.Tag = item.Tag;
			}
		}

		private void RepeatLastEffect(object sender, EventArgs e)
		{
			ToolStripItem item = (ToolStripItem)sender;
			if (item.Tag != null)
			{
				RunPhotoshopFilterThread((PluginData)item.Tag, true);
			}
		}

		private void SetApplyFilterText()
		{
			if (base.InvokeRequired)
			{
				base.Invoke(new Action<string>(delegate(string text)
				{
					this.toolStripStatusLabel1.Text = string.Format(Resources.ApplyFilterStatusFormat, this.filterName);
					this.toolStripProgressBar1.Visible = true;
				}), new object[] { filterName });
			}
			else
			{
				this.toolStripStatusLabel1.Text = string.Format(Resources.ApplyFilterStatusFormat, this.filterName);
				this.toolStripProgressBar1.Visible = true;
			}
			Application.DoEvents();
		}

		private void UpdateFilterProgress(object sender, FilterProgressEventArgs e)
		{
			if (!setFilterApplyText)
			{
				SetApplyFilterText();
				this.setFilterApplyText = true;
			}

			if (base.InvokeRequired)
			{
				base.Invoke(new Action<int>(delegate(int value)
				{
					this.toolStripProgressBar1.Value = value;
				}), new object[] { e.Progress });
			}
			else
			{
				this.toolStripProgressBar1.Value = e.Progress;
			}
		}

		private void openToolStripMenuItem_Click(object sender, EventArgs e)
		{
			if (openFileDialog1.ShowDialog(this) == System.Windows.Forms.DialogResult.OK)
			{
				try
				{
					this.OpenFile(openFileDialog1.FileName);
				}
				catch (FileNotFoundException ex)
				{
					ShowErrorMessage(ex.Message);
				}
				catch (FileFormatException ex)
				{
					ShowErrorMessage(ex.Message);
				}
				catch (UnauthorizedAccessException ex)
				{
					ShowErrorMessage(ex.Message);
				}
			}
		}

		private void OpenFile(string path)
		{
			this.Cursor = Cursors.WaitCursor;
			try
			{
				BitmapFrame frame = BitmapFrame.Create(new Uri(path), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.None);

				srcImage = frame.Clone();
				srcImage.Freeze();

				PixelFormat format = srcImage.Format;
				int channelCount = format.Masks.Count;
				int bitsPerChannel = format.BitsPerPixel / channelCount;

				this.imageFileName = Path.GetFileName(path);

				if (format == PixelFormats.BlackWhite || format == PixelFormats.Gray2 || format == PixelFormats.Gray4 || format == PixelFormats.Gray8 ||
					format == PixelFormats.Gray16 || format == PixelFormats.Gray32Float)
				{
					this.imageType = "Gray/";
				}
				else
				{
					this.imageType = "RGB/";
				}

				// Set the meta data manually as some codecs may not implement all the properties required for BitmapMetadata.Clone() to succeed.
				BitmapMetadata metaData = null;

				try
				{
					metaData = srcImage.Metadata as BitmapMetadata;
				}
				catch (NotSupportedException)
				{
				}

				this.panel1.SuspendLayout();

				if (bitsPerChannel >= 16)
				{
					FormatConvertedBitmap conv = new FormatConvertedBitmap();
					conv.BeginInit();
					conv.Source = srcImage;
					conv.DestinationFormat = channelCount == 4 ? PixelFormats.Bgra32 : PixelFormats.Bgr24;
					conv.EndInit();

					using (MemoryStream ms = new MemoryStream())
					{
						PngBitmapEncoder enc = new PngBitmapEncoder();
						enc.Frames.Add(BitmapFrame.Create(conv, null, metaData, null));
						enc.Save(ms);

						this.canvas.Surface = new Bitmap(ms, true);
					}

					this.imageType += "16";
				}
				else
				{
					using (MemoryStream ms = new MemoryStream())
					{
						PngBitmapEncoder enc = new PngBitmapEncoder();
						enc.Frames.Add(BitmapFrame.Create(srcImage, null, metaData, null));
						enc.Save(ms);

						this.canvas.Surface = new Bitmap(ms, true);
					}

					this.imageType += "8";
				}

				this.panelClientSize = this.panel1.ClientSize;

				if (canvas.Size.Width > panel1.ClientSize.Width ||
					canvas.Size.Height > panel1.ClientSize.Height)
				{
					// Calculate the new client size with the scrollbars manually so we can resize before they appear.
					Size clientSize = panel1.ClientSize;

					if (canvas.Size.Width > clientSize.Width)
					{
						clientSize.Width -= SystemInformation.VerticalScrollBarWidth;
					}

					if (canvas.Size.Height > clientSize.Height)
					{
						clientSize.Height -= SystemInformation.HorizontalScrollBarHeight;
					}

					this.panelClientSize = clientSize;
					this.canvas.ZoomToWindow(clientSize);
				}
				else
				{
					this.Text = string.Format(Resources.TitleStringFormat, new object[] { this.titleString, this.imageFileName, 100, this.imageType });

					this.zoomInBtn.Enabled = this.canvas.CanZoomIn();
					this.zoomOutBtn.Enabled = this.canvas.CanZoomOut();
					this.zoomToWindowBtn.Enabled = this.canvas.CanZoomToWindow(this.panel1.ClientSize);
					this.zoomToActualSizeBtn.Enabled = this.canvas.CanZoomToActualSize();
				}

				this.panel1.ResumeLayout(true);

				EnableFiltersForImageFormat();

				this.pointerSelectBtn.Enabled = true;
				this.rectangleSelectBtn.Enabled = true;
				this.elipseSelectBtn.Enabled = true;

				this.historyStack.Clear();

				this.canvas.IsDirty = false;
				this.dstImage = null;

				this.hostInfo.Title = this.imageFileName;

				if (!string.IsNullOrEmpty(srcImageTempFileName))
				{
					File.Delete(srcImageTempFileName);
					this.srcImageTempFileName = string.Empty;
				}
			}
			catch (Exception)
			{
				throw;
			}
			finally
			{
				this.Cursor = Cursors.Default;
			}
		}

		private void saveToolStripMenuItem_Click(object sender, EventArgs e)
		{
			if (dstImage != null)
			{
				int bitsPerChannel = dstImage.Format.BitsPerPixel / dstImage.Format.Masks.Count;

				if (bitsPerChannel >= 16)
				{
					this.saveFileDialog1.Filter = "PNG Image (*.png)|*.png|TIFF Image (*.tif, *.tiff)|*.tif;*.tiff|Windows Media Photo (*.wdp, *.jxr)|*.wdp;*.jxr";
					this.saveFileDialog1.FilterIndex = 1;
				}
				else
				{
					this.saveFileDialog1.Filter = "Bitmap Image (*.bmp)|*.bmp|GIF Image (*.gif)|*.gif|JPEG Image (*.jpg, *.jpe, *.jpeg, *.jfif)|*.jpg;*.jpe;*.jpeg;*.jfif|PNG Image (*.png)|*.png|TIFF Image (*.tif, *.tiff)|*.tif;*.tiff|Windows Media Photo (*.wdp, *.jxr)|*.wdp;*.jxr";
					this.saveFileDialog1.FilterIndex = 4;
				}

				if (saveFileDialog1.ShowDialog(this) == System.Windows.Forms.DialogResult.OK)
				{
					string path = this.saveFileDialog1.FileName;
					string ext = Path.GetExtension(path).ToLowerInvariant();
					BitmapEncoder encoder = null;

					if (ext == ".bmp")
					{
						encoder = new BmpBitmapEncoder();
					}
					else if (ext == ".gif")
					{
						encoder = new GifBitmapEncoder();
					}
					else if (ext == ".jpg" || ext == ".jpeg" || ext == ".jpe" || ext == ".jiff")
					{
						encoder = new JpegBitmapEncoder();
					}
					else if (ext == ".png")
					{
						encoder = new PngBitmapEncoder();
					}
					else if (ext == ".tif" || ext == ".tiff")
					{
						encoder = new TiffBitmapEncoder();
					}
					else if (ext == ".wdp" || ext == ".jxr")
					{
						encoder = new WmpBitmapEncoder();
					}

					BitmapMetadata metaData = null;

					try
					{
						metaData = this.srcImage.Metadata as BitmapMetadata;
					}
					catch (NotSupportedException)
					{
					}

					if (metaData != null)
					{
						metaData = MetaDataHelper.ConvertSaveMetaDataFormat(metaData, encoder);
					}

					encoder.Frames.Add(BitmapFrame.Create(dstImage, null, metaData, null));

					using (FileStream fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
					{
						encoder.Save(fs);
					}

					this.canvas.IsDirty = false;
				}
			}
		}
			
		private void pointerSelectBtn_Click(object sender, EventArgs e)
		{
			if (!pointerSelectBtn.Checked)
			{
				this.rectangleSelectBtn.Checked = false;
				this.elipseSelectBtn.Checked = false;
				this.pointerSelectBtn.Checked = true;
				this.toolStripStatusLabel1.Text = string.Empty;
			}

			if (canvas.SelectionType != null)
			{
				this.canvas.SelectionType = null;
			}
		}

		private void rectangleSelectBtn_Click(object sender, EventArgs e)
		{
			if (!rectangleSelectBtn.Checked)
			{
				this.pointerSelectBtn.Checked = false;
				this.elipseSelectBtn.Checked = false;
				this.rectangleSelectBtn.Checked = true;
				this.toolStripStatusLabel1.Text = Resources.RectangleSelectionToolStatusText;
			}

			if ((canvas.SelectionType == null) || canvas.SelectionType.GetType() != typeof(RectangleSelectTool))
			{
				this.canvas.SelectionType = new RectangleSelectTool();
			}
		}

		private void elipseSelectBtn_Click(object sender, EventArgs e)
		{
			if (!elipseSelectBtn.Checked)
			{
				this.pointerSelectBtn.Checked = false;
				this.rectangleSelectBtn.Checked = false;
				this.elipseSelectBtn.Checked = true;
				this.toolStripStatusLabel1.Text = Resources.EllipseSelectionToolStatusText;
			}

			if ((canvas.SelectionType == null) || canvas.SelectionType.GetType() != typeof(EllipseSelectTool))
			{
				this.canvas.SelectionType = new EllipseSelectTool();
			}
		}

		private void zoomInBtn_Click(object sender, EventArgs e)
		{
			this.canvas.ZoomIn();
		}

		private void zoomOutBtn_Click(object sender, EventArgs e)
		{
			this.canvas.ZoomOut();
		} 
			
		private void zoomToWindowBtn_Click(object sender, EventArgs e)
		{
			if (!panelClientSize.IsEmpty)
			{
				this.canvas.ZoomToWindow(panelClientSize);
			}
		}

		private void zoomToActualSizeBtn_Click(object sender, EventArgs e)
		{
			this.canvas.ZoomToActualSize();
		}

		private void canvas_ZoomChanged(object sender, CanvasZoomChangedEventArgs e)
		{
			this.zoomOutBtn.Enabled = this.canvas.CanZoomOut();
			this.zoomInBtn.Enabled = this.canvas.CanZoomIn();

			this.zoomToActualSizeBtn.Enabled = this.canvas.CanZoomToActualSize();
			this.zoomToWindowBtn.Enabled = this.canvas.CanZoomToWindow(panelClientSize);

			int percent = 0;
			if (e.NewZoom < 0.10f)
			{
				percent = (int)Math.Round(e.NewZoom * 1000f);
			}
			else
			{
				percent = (int)Math.Round(e.NewZoom * 100f);
			}

			this.Text = string.Format(Resources.TitleStringFormat, new object[] { this.titleString, this.imageFileName, percent, this.imageType });
		}


		private void undoToolStripMenuItem_Click(object sender, EventArgs e)
		{
			this.historyStack.StepBackward(this.canvas, ref this.dstImage);
		}

		private void redoToolStripMenuItem_Click(object sender, EventArgs e)
		{
			this.historyStack.StepForward(this.canvas, ref this.dstImage);
		}

		private void primaryColorBtn_Click(object sender, EventArgs e)
		{
			this.colorDialog1.Color = this.primaryColorBtn.RectangleColor;
			if (colorDialog1.ShowDialog(this) == System.Windows.Forms.DialogResult.OK)
			{
				this.primaryColorBtn.RectangleColor = colorDialog1.Color;
			}
		}

		private void secondaryColorBtn_Click(object sender, EventArgs e)
		{
			this.colorDialog1.Color = this.secondaryColorBtn.RectangleColor;
			if (colorDialog1.ShowDialog(this) == System.Windows.Forms.DialogResult.OK)
			{
				this.secondaryColorBtn.RectangleColor = colorDialog1.Color;
			}
		}

		protected override void OnFormClosing(FormClosingEventArgs e)
		{
			base.OnFormClosing(e);

			if (filterThread != null)
			{
				e.Cancel = true;
				return;
			}

			if (canvas.IsDirty)
			{
				TaskButton save = new TaskButton(Resources.saveHS, Resources.SaveChangesText, Resources.SaveChangesDescription);
				TaskButton discard = new TaskButton(Resources.FileClose, Resources.DontSaveChangesText, Resources.DontSaveChangesDescription);
				TaskButton cancel = new TaskButton(Resources.CancelIcon, Resources.CancelText, Resources.CancelDescription);
				string actionText = string.Format(Resources.UnsavedChangesText, this.imageFileName);

				int width96 = (TaskDialog.DefaultPixelWidth96Dpi * 4) / 3; // 33% larger

				using (Bitmap bmp = this.canvas.ResizeCopy(96, 96))
				{
					TaskButton result = TaskDialog.Show(this, Resources.Warning, Resources.SaveChangesCaption, bmp, true, actionText,
								new TaskButton[] { save, discard, cancel }, save, cancel, width96);

					if (result == save)
					{
						this.saveToolStripMenuItem_Click(this, EventArgs.Empty);
					}
					else if (result == cancel)
					{
						e.Cancel = true;
					}
				}
			}
		}

		private void exitToolStripMenuItem_Click(object sender, EventArgs e)
		{
			this.Close();
		}

		protected override void OnResize(EventArgs e)
		{
			base.OnResize(e);

			if (base.WindowState == FormWindowState.Maximized && this.canvas.IsActualSize)
			{
				// If the window is maximized with the canvas at 100% do not resize the canvas.
				return;
			}

			if (!panel1.ClientSize.IsEmpty && panelClientSize != panel1.ClientSize && canvas.Surface != null)
			{
				this.canvas.ResetSize();
				this.panelClientSize = panel1.ClientSize;

				this.zoomToWindowBtn_Click(this, EventArgs.Empty);
			}
		}

		private void canvas_DirtyChanged(object sender, CanvasDirtyChangedEventArgs e)
		{
			this.saveToolStripMenuItem.Enabled = e.Dirty; 
		}

		private string dropImageFileName;
		protected override void OnDragEnter(DragEventArgs drgevent)
		{
			base.OnDragEnter(drgevent);

			this.dropImageFileName = string.Empty;
			if (drgevent.Data.GetDataPresent(DataFormats.FileDrop, false))
			{
				string[] files = drgevent.Data.GetData(DataFormats.FileDrop, false) as string[];

				if ((files.Length == 1) && ImageFileExtensions.Contains(Path.GetExtension(files[0]), StringComparer.OrdinalIgnoreCase) && (File.GetAttributes(files[0]) & FileAttributes.Directory) == 0)
				{
					drgevent.Effect = DragDropEffects.Copy;
					this.dropImageFileName = files[0];
				}
			}
		}

		protected override void OnDragDrop(DragEventArgs drgevent)
		{
			base.OnDragDrop(drgevent);

			if (!string.IsNullOrEmpty(dropImageFileName))
			{
				try
				{
					this.OpenFile(dropImageFileName);
				}
				catch (FileNotFoundException ex)
				{
					ShowErrorMessage(ex.Message);
				}
				catch (FileFormatException ex)
				{
					ShowErrorMessage(ex.Message);
				}
				catch (UnauthorizedAccessException ex)
				{
					ShowErrorMessage(ex.Message);
				}
			}
		}

		private void aboutToolStripMenuItem_Click(object sender, EventArgs e)
		{
			using (AboutBox box = new AboutBox())
			{
				box.ShowDialog(this);
			}
		}

		private void refreshFiltersToolStripMenuItem_Click(object sender, EventArgs e)
		{
			if (!string.IsNullOrEmpty(currentPluginDirectory))
			{
				QueryDirectory(currentPluginDirectory);
			}
		}

		private sealed class WorkerArgs
		{
			internal string Path
			{
				get;
				private set;
			}
			internal ToolStripMenuItemEx[] Filters
			{
				get;
				set;
			}
			internal ToolStripItem[] AboutFilters
			{
				get;
				set;
			}

			internal WorkerArgs(string path)
			{
				this.Path = path;
				this.Filters = null;
				this.AboutFilters = null;
			}
		}

		private void backgroundWorker1_DoWork(object sender, System.ComponentModel.DoWorkEventArgs e)
		{
			WorkerArgs args = (WorkerArgs)e.Argument;

			Dictionary<string, ToolStripMenuItemEx> filterList = new Dictionary<string, ToolStripMenuItemEx>();
			List<ToolStripItem> aboutList = new List<ToolStripItem>();

			foreach (var plug in PSFilterHost.EnumerateFilters(args.Path, true))
			{
				ToolStripMenuItem child = new ToolStripMenuItem(plug.Title, null, new EventHandler(RunPhotoshopFilter_Click)) { Name = plug.Title, Tag = plug };
				ToolStripMenuItem aboutItem = new ToolStripMenuItem(plug.Title, null, new EventHandler(ShowFilterAboutDialog)) { Tag = plug };

				if (filterList.ContainsKey(plug.Category))
				{
					ToolStripMenuItemEx parent = filterList[plug.Category];

					if (!parent.SubMenuItems.ContainsKey(plug.Title))
					{
						parent.SubMenuItems.Add(child);
						if (plug.HasAboutBox)
						{
							aboutList.Add(aboutItem); 
						}
					}
				}
				else
				{
					ToolStripMenuItemEx parent = new ToolStripMenuItemEx(plug.Category, child);
					filterList.Add(plug.Category, parent);
					if (plug.HasAboutBox)
					{
						aboutList.Add(aboutItem); 
					}
				}
			}

			if (filterList.Count > 0)
			{
				ToolStripMenuItemEx[] filters = new ToolStripMenuItemEx[filterList.Values.Count];
				filterList.Values.CopyTo(filters, 0);

				ToolStripItemComparer comparer = new ToolStripItemComparer();

				Array.Sort<ToolStripItem>(filters, comparer);

				// sort the items in the sub menus.
				for (int i = 0; i < filters.Length; i++)
				{
					filters[i].SubMenuItems.Sort(comparer);
				}

				aboutList.Sort(comparer);

				args.Filters = filters;
				args.AboutFilters = aboutList.ToArray();
			}
			
			e.Result = args;
		}

		private void backgroundWorker1_RunWorkerCompleted(object sender, System.ComponentModel.RunWorkerCompletedEventArgs e)
		{
			if (e.Error != null)
			{
				ShowErrorMessage(e.Error.Message);
			}
			else
			{
				WorkerArgs args = (WorkerArgs)e.Result;
				if (args.Filters != null)
				{
					this.filtersToolStripMenuItem.DropDownItems.AddRange(args.Filters);

					if (args.AboutFilters.Length > 0)
					{
						this.aboutPluginsMenuItem.DropDownItems.AddRange(args.AboutFilters);
						if (!aboutPluginsMenuItem.Available)
						{
							this.aboutMenuToolStripSeparator.Available = true;
							this.aboutPluginsMenuItem.Available = true;
						} 
					}
					else
					{
						this.aboutMenuToolStripSeparator.Available = false;
						this.aboutPluginsMenuItem.Available = false;
					}

					EnableFiltersForImageFormat();

					this.toolStripStatusLabel1.Text = string.Empty;
					this.currentPluginDirectory = args.Path;
					this.refreshFiltersToolStripMenuItem.Enabled = true;
				}
				else
				{
					this.toolStripStatusLabel1.Text = Resources.NoFiltersStatusText;
					this.refreshFiltersToolStripMenuItem.Enabled = false;
					this.currentPluginDirectory = string.Empty;

					if (aboutPluginsMenuItem.Available)
					{
						this.aboutMenuToolStripSeparator.Available = false;
						this.aboutPluginsMenuItem.Available = false;
					}
				}
			}

			this.Cursor = Cursors.Default;
		}

		private ColorPickerResult PickColor(string prompt, byte defaultRed, byte defaultGreen, byte defaultBlue)
		{
			ColorPickerResult color = null;

			using (ColorPickerForm dialog = new ColorPickerForm(prompt))
			{
				dialog.SetDefaultColor(defaultRed, defaultGreen, defaultBlue);

				if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
				{
					color = new ColorPickerResult(dialog.UserPrimaryColor);
				}

			}

			return color;
		}
	}
}
