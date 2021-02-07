﻿using LiveSplit.Model;
using LoadDetector;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Formatters.Binary;
using System.Text;
using System.Windows.Forms;
using System.Xml;

namespace LiveSplit.UI.Components
{
    public partial class BlackScreenDetectorComponentSettings : UserControl
    {
        #region Public Fields

        public bool AutoSplitterEnabled = false;

        public bool AutoSplitterDisableOnSkipUntilSplit = false;

        public bool RemoveFadeouts = false;
        public bool RemoveFadeins = false;

        public bool SaveDetectionLog = false;

        public bool RecordImages = false;

        public float MinimumBlackLength = 0.0f;
        public int BlackLevel = 10;

        public int AverageBlackLevel = -1;

        public string DetectionLogFolderName = "BlackScreenDetectorLog";

        //Number of frames to wait for a change from load -> running and vice versa.
        public int AutoSplitterJitterToleranceFrames = 8;

        //If you split manually during "AutoSplitter" mode, I ignore AutoSplitter-splits for 50 frames. (A little less than 2 seconds)
        //This means that if a split would happen during these frames, it is ignored.
        public int AutoSplitterManualSplitDelayFrames = 50;

        #endregion Public Fields

        #region Private Fields

        private AutoSplitData autoSplitData = null;

        private readonly float captureAspectRatioX = 16.0f;

        private readonly float captureAspectRatioY = 9.0f;

        private List<string> captureIDs = null;

        private Size captureSize = new Size(300, 100);

        private readonly float cropOffsetX = 0.0f;

        private readonly float cropOffsetY = -460.0f;

        private bool drawingPreview = false;

        private readonly List<Control> dynamicAutoSplitterControls;

        private readonly float featureVectorResolutionX = 1920.0f;

        private readonly float featureVectorResolutionY = 1080.0f;

        private ImageCaptureInfo imageCaptureInfo;

        private Bitmap lastDiagnosticCapture = null;

        private List<int> lastFeatures = null;

        private Bitmap lastFullCapture = null;

        private Bitmap lastFullCroppedCapture = null;

        private int lastMatchingBins = 0;

        private readonly LiveSplitState liveSplitState = null;

        //private string DiagnosticsFolderName = "CrashNSTDiagnostics/";
        private int numCaptures = 0;

        private int numScreens = 1;

        private readonly Dictionary<string, XmlElement> AllGameAutoSplitSettings;

        private Bitmap previewImage = null;

        //-1 -> full screen, otherwise index process list
        private int processCaptureIndex = -1;

        private Process[] processList;
        private int scalingValue = 100;
        private float scalingValueFloat = 1.0f;
        private string selectedCaptureID = "";
        private Point selectionBottomRight = new Point(0, 0);
        private Rectangle selectionRectanglePreviewBox;
        private Point selectionTopLeft = new Point(0, 0);

        #endregion Private Fields

        #region Public Constructors

        public class Binder : System.Runtime.Serialization.SerializationBinder
        {
            public override Type BindToType(string assemblyName, string typeName)
            {
                Assembly ass = Assembly.Load(assemblyName);
                return ass.GetType(typeName);
            }
        }

        private void DeserializeAndUpdateDetectorData()
        {
            // TODO: hardcode this for FF7R. Potentially capture full window? not sure about performance...

            //DetectorData data = DeserializeDetectorData(LoadRemoverDataName);

            DetectorData data = new DetectorData
            {
                sizeX = 600,
                sizeY = 100,
                numPatchesX = 12,
                numPatchesY = 2
            };
            captureSize.Width = data.sizeX;
            captureSize.Height = data.sizeY;

            FeatureDetector.numberOfBins = 16;
            FeatureDetector.patchSizeX = captureSize.Width / data.numPatchesX;
            FeatureDetector.patchSizeY = captureSize.Height / data.numPatchesY;
        }

        public BlackScreenDetectorComponentSettings(LiveSplitState state)
        {
            InitializeComponent();

            //RemoveFadeins = chkRemoveFadeIns.Checked;
            DeserializeAndUpdateDetectorData();

            RemoveFadeouts = chkRemoveTransitions.Checked;
            RemoveFadeins = chkRemoveTransitions.Checked;
            SaveDetectionLog = chkSaveDetectionLog.Checked;

            AllGameAutoSplitSettings = new Dictionary<string, XmlElement>();
            dynamicAutoSplitterControls = new List<Control>();
            CreateAutoSplitControls(state);
            liveSplitState = state;
            InitImageCaptureInfo();
            //processListComboBox.SelectedIndex = 0;
            lblVersion.Text = "v" + Assembly.GetExecutingAssembly().GetName().Version.ToString(3);

            RefreshCaptureWindowList();
            //processListComboBox.SelectedIndex = 0;
            DrawPreview();
        }

        #endregion Public Constructors

        #region Public Methods

        public void SetBlackLevel(int black_level)
        {
            AverageBlackLevel = black_level;
            lblBlackLevel.Text = "Black-Level: " + AverageBlackLevel;
        }

        public Bitmap CaptureImage()
        {
            Bitmap b = new Bitmap(1, 1);

            //Full screen capture
            if (processCaptureIndex < 0)
            {
                Screen selected_screen = Screen.AllScreens[-processCaptureIndex - 1];
                Rectangle screenRect = selected_screen.Bounds;

                screenRect.Width = (int)(screenRect.Width * scalingValueFloat);
                screenRect.Height = (int)(screenRect.Height * scalingValueFloat);

                //Change size according to selected crop
                screenRect.Width = (int)(imageCaptureInfo.CropCoordinateRight - imageCaptureInfo.CropCoordinateLeft);
                screenRect.Height = (int)(imageCaptureInfo.CropCoordinateBottom - imageCaptureInfo.CropCoordinateTop);

                //Compute crop coordinates and width/ height based on resoution
                ImageCapture.SizeAdjustedCropAndOffset(screenRect.Width, screenRect.Height, ref imageCaptureInfo);

                //Adjust for crop offset
                imageCaptureInfo.FrameCenterX += imageCaptureInfo.CropCoordinateLeft;
                imageCaptureInfo.FrameCenterY += imageCaptureInfo.CropCoordinateTop;

                //Adjust for selected screen offset
                imageCaptureInfo.FrameCenterX += selected_screen.Bounds.X;
                imageCaptureInfo.FrameCenterY += selected_screen.Bounds.Y;

                b = ImageCapture.CaptureFromDisplay(ref imageCaptureInfo);
            }
            else
            {
                IntPtr handle = new IntPtr(0);

                if (processCaptureIndex >= processList.Length)
                    return b;

                if (processCaptureIndex != -1)
                {
                    handle = processList[processCaptureIndex].MainWindowHandle;
                }
                //Capture from specific process
                processList[processCaptureIndex].Refresh();
                if ((int)handle == 0)
                    return b;

                b = ImageCapture.PrintWindow(handle, ref imageCaptureInfo, full: true, useCrop: false);
            }

            return b;
        }

        public Bitmap CaptureImageFullPreview(ref ImageCaptureInfo imageCaptureInfo, bool useCrop = false)
        {
            Bitmap b = new Bitmap(1, 1);

            //Full screen capture
            if (processCaptureIndex < 0)
            {
                Screen selected_screen = Screen.AllScreens[-processCaptureIndex - 1];
                Rectangle screenRect = selected_screen.Bounds;

                screenRect.Width = (int)(screenRect.Width * scalingValueFloat);
                screenRect.Height = (int)(screenRect.Height * scalingValueFloat);

                if (useCrop)
                {
                    //Change size according to selected crop
                    screenRect.Width = (int)(imageCaptureInfo.CropCoordinateRight - imageCaptureInfo.CropCoordinateLeft);
                    screenRect.Height = (int)(imageCaptureInfo.CropCoordinateBottom - imageCaptureInfo.CropCoordinateTop);
                }

                //Compute crop coordinates and width/ height based on resoution
                ImageCapture.SizeAdjustedCropAndOffset(screenRect.Width, screenRect.Height, ref imageCaptureInfo);

                imageCaptureInfo.ActualCropSizeX = 2 * imageCaptureInfo.FrameCenterX;
                imageCaptureInfo.ActualCropSizeY = 2 * imageCaptureInfo.FrameCenterY;

                if (useCrop)
                {
                    //Adjust for crop offset
                    imageCaptureInfo.FrameCenterX += imageCaptureInfo.CropCoordinateLeft;
                    imageCaptureInfo.FrameCenterY += imageCaptureInfo.CropCoordinateTop;
                }

                //Adjust for selected screen offset
                imageCaptureInfo.FrameCenterX += selected_screen.Bounds.X;
                imageCaptureInfo.FrameCenterY += selected_screen.Bounds.Y;

                imageCaptureInfo.ActualOffsetX = 0;
                imageCaptureInfo.ActualOffsetY = 0;

                b = ImageCapture.CaptureFromDisplay(ref imageCaptureInfo);

                imageCaptureInfo.ActualOffsetX = cropOffsetX;
                imageCaptureInfo.ActualOffsetY = cropOffsetY;
            }
            else
            {
                IntPtr handle = new IntPtr(0);

                if (processCaptureIndex >= processList.Length)
                    return b;

                if (processCaptureIndex != -1)
                {
                    handle = processList[processCaptureIndex].MainWindowHandle;
                }
                //Capture from specific process
                processList[processCaptureIndex].Refresh();
                if ((int)handle == 0)
                    return b;

                b = ImageCapture.PrintWindow(handle, ref imageCaptureInfo, full: true, useCrop: useCrop, scalingValueFloat: scalingValueFloat);
            }

            return b;
        }

        public void ChangeAutoSplitSettingsToGameName(string gameName, string category)
        {
            gameName = RemoveInvalidXMLCharacters(gameName);
            category = RemoveInvalidXMLCharacters(category);

            //TODO: go through gameSettings to see if the game matches, enter info based on that.
            foreach (var control in dynamicAutoSplitterControls)
            {
                tabPage2.Controls.Remove(control);
            }

            dynamicAutoSplitterControls.Clear();

            //Add current game to gameSettings
            XmlDocument document = new XmlDocument();

            var gameNode = document.CreateElement(autoSplitData.GameName + autoSplitData.Category);

            //var categoryNode = document.CreateElement(autoSplitData.Category);

            foreach (AutoSplitEntry splitEntry in autoSplitData.SplitData)
            {
                gameNode.AppendChild(ToElement(document, splitEntry.SplitName, splitEntry.NumberOfLoads));
            }

            AllGameAutoSplitSettings[autoSplitData.GameName + autoSplitData.Category] = gameNode;

            //otherGameSettings[]

            CreateAutoSplitControls(liveSplitState);

            //Change controls if we find the chosen game
            foreach (var gameSettings in AllGameAutoSplitSettings)
            {
                if (gameSettings.Key == gameName + category)
                {
                    var game_element = gameSettings.Value;

                    //var splits_element = game_element[autoSplitData.Category];
                    Dictionary<string, int> usedSplitNames = new Dictionary<string, int>();
                    foreach (XmlElement number_of_loads in game_element)
                    {
                        var up_down_controls = tabPage2.Controls.Find(number_of_loads.LocalName, true);

                        if (usedSplitNames.ContainsKey(number_of_loads.LocalName) == false)
                        {
                            usedSplitNames[number_of_loads.LocalName] = 0;
                        }
                        else
                        {
                            usedSplitNames[number_of_loads.LocalName]++;
                        }

                        //var up_down = tabPage2.Controls.Find(number_of_loads.LocalName, true).FirstOrDefault() as NumericUpDown;

                        NumericUpDown up_down = (NumericUpDown)up_down_controls[usedSplitNames[number_of_loads.LocalName]];

                        if (up_down != null)
                        {
                            up_down.Value = Convert.ToInt32(number_of_loads.InnerText);
                        }
                    }
                }
            }
        }

        public int GetCumulativeNumberOfLoadsForSplit(string splitName)
        {
            int numberOfLoads = 0;
            splitName = RemoveInvalidXMLCharacters(splitName);
            foreach (AutoSplitEntry entry in autoSplitData.SplitData)
            {
                numberOfLoads += entry.NumberOfLoads;
                if (entry.SplitName == splitName)
                {
                    return numberOfLoads;
                }
            }
            return numberOfLoads;
        }

        public int GetAutoSplitNumberOfLoadsForSplit(string splitName)
        {
            splitName = RemoveInvalidXMLCharacters(splitName);
            foreach (AutoSplitEntry entry in autoSplitData.SplitData)
            {
                if (entry.SplitName == splitName)
                {
                    return entry.NumberOfLoads;
                }
            }

            //This should never happen, but might if the splits are changed without reloading the component...
            return 2;
        }

        public XmlNode GetSettings(XmlDocument document)
        {
            //RefreshCaptureWindowList();
            var settingsNode = document.CreateElement("Settings");

            settingsNode.AppendChild(ToElement(document, "Version", Assembly.GetExecutingAssembly().GetName().Version.ToString(3)));

            settingsNode.AppendChild(ToElement(document, "MinimumBlackLength", MinimumBlackLength));

            settingsNode.AppendChild(ToElement(document, "BlackLevel", BlackLevel));

            if (captureIDs != null)
            {
                if (processListComboBox.SelectedIndex < captureIDs.Count && processListComboBox.SelectedIndex >= 0)
                {
                    var selectedCaptureTitle = captureIDs[processListComboBox.SelectedIndex];

                    settingsNode.AppendChild(ToElement(document, "SelectedCaptureTitle", selectedCaptureTitle));
                }
            }

            settingsNode.AppendChild(ToElement(document, "ScalingPercent", trackBar1.Value));

            var captureRegionNode = document.CreateElement("CaptureRegion");

            captureRegionNode.AppendChild(ToElement(document, "X", selectionRectanglePreviewBox.X));
            captureRegionNode.AppendChild(ToElement(document, "Y", selectionRectanglePreviewBox.Y));
            captureRegionNode.AppendChild(ToElement(document, "Width", selectionRectanglePreviewBox.Width));
            captureRegionNode.AppendChild(ToElement(document, "Height", selectionRectanglePreviewBox.Height));

            settingsNode.AppendChild(captureRegionNode);

            settingsNode.AppendChild(ToElement(document, "AutoSplitEnabled", enableAutoSplitterChk.Checked));
            settingsNode.AppendChild(ToElement(document, "AutoSplitDisableOnSkipUntilSplit", chkAutoSplitterDisableOnSkip.Checked));
            settingsNode.AppendChild(ToElement(document, "RemoveFadeouts", chkRemoveTransitions.Checked));
            //settingsNode.AppendChild(ToElement(document, "RemoveFadeins", chkRemoveFadeIns.Checked));
            settingsNode.AppendChild(ToElement(document, "SaveDetectionLog", chkSaveDetectionLog.Checked));

            var splitsNode = document.CreateElement("AutoSplitGames");

            //Re-Add all other games/categories to the xml file
            foreach (var gameSettings in AllGameAutoSplitSettings)
            {
                if (gameSettings.Key != autoSplitData.GameName + autoSplitData.Category)
                {
                    XmlNode node = document.ImportNode(gameSettings.Value, true);
                    splitsNode.AppendChild(node);
                }
            }

            var gameNode = document.CreateElement(autoSplitData.GameName + autoSplitData.Category);

            //var categoryNode = document.CreateElement(autoSplitData.Category);

            foreach (AutoSplitEntry splitEntry in autoSplitData.SplitData)
            {
                gameNode.AppendChild(ToElement(document, splitEntry.SplitName, splitEntry.NumberOfLoads));
            }
            AllGameAutoSplitSettings[autoSplitData.GameName + autoSplitData.Category] = gameNode;
            //gameNode.AppendChild(categoryNode);
            splitsNode.AppendChild(gameNode);
            settingsNode.AppendChild(splitsNode);
            //settingsNode.AppendChild(ToElement(document, "AutoReset", AutoReset.ToString()));
            //settingsNode.AppendChild(ToElement(document, "Category", category.ToString()));
            /*if (checkedListBox1.Items.Count == SplitsByCategory[category].Length)
			{
				for (int i = 0; i < checkedListBox1.Items.Count; i++)
				{
					SplitsByCategory[category][i].enabled = (checkedListBox1.GetItemCheckState(i) == CheckState.Checked);
				}
			}

			foreach (Split[] category in SplitsByCategory)
			{
				foreach (Split split in category)
				{
					settingsNode.AppendChild(ToElement(document, "split_" + split.splitID, split.enabled.ToString()));
				}
			}*/

            return settingsNode;
        }

        public void SetSettings(XmlNode settings)
        {
            var element = (XmlElement)settings;
            if (!element.IsEmpty)
            {
                if (element["MinimumBlackLength"] != null)
                {
                    requiredMatchesUpDown.Value = Convert.ToDecimal(element["MinimumBlackLength"].InnerText);
                }

                if (element["BlackLevel"] != null)
                {
                    blackLevelNumericUpDown.Value = Convert.ToDecimal(element["BlackLevel"].InnerText);
                }

                if (element["SelectedCaptureTitle"] != null)
                {
                    String selectedCaptureTitle = element["SelectedCaptureTitle"].InnerText;
                    selectedCaptureID = selectedCaptureTitle;
                    UpdateIndexToCaptureID();
                    RefreshCaptureWindowList();
                }

                if (element["ScalingPercent"] != null)
                {
                    trackBar1.Value = Convert.ToInt32(element["ScalingPercent"].InnerText);
                }

                if (element["CaptureRegion"] != null)
                {
                    var element_region = element["CaptureRegion"];
                    if (element_region["X"] != null && element_region["Y"] != null && element_region["Width"] != null && element_region["Height"] != null)
                    {
                        int captureRegionX = Convert.ToInt32(element_region["X"].InnerText);
                        int captureRegionY = Convert.ToInt32(element_region["Y"].InnerText);
                        int captureRegionWidth = Convert.ToInt32(element_region["Width"].InnerText);
                        int captureRegionHeight = Convert.ToInt32(element_region["Height"].InnerText);

                        selectionRectanglePreviewBox = new Rectangle(captureRegionX, captureRegionY, captureRegionWidth, captureRegionHeight);
                        selectionTopLeft = new Point(captureRegionX, captureRegionY);
                        selectionBottomRight = new Point(captureRegionX + captureRegionWidth, captureRegionY + captureRegionHeight);

                        //RefreshCaptureWindowList();
                    }
                }

                /*foreach (Split[] category in SplitsByCategory)
				{
					foreach (Split split in category)
					{
						if (element["split_" + split.splitID] != null)
						{
							split.enabled = Convert.ToBoolean(element["split_" + split.splitID].InnerText);
						}
					}
				}*/
                if (element["AutoSplitEnabled"] != null)
                {
                    enableAutoSplitterChk.Checked = Convert.ToBoolean(element["AutoSplitEnabled"].InnerText);
                }

                if (element["AutoSplitDisableOnSkipUntilSplit"] != null)
                {
                    chkAutoSplitterDisableOnSkip.Checked = Convert.ToBoolean(element["AutoSplitDisableOnSkipUntilSplit"].InnerText);
                }

                if (element["RemoveFadeouts"] != null)
                {
                    chkRemoveTransitions.Checked = Convert.ToBoolean(element["RemoveFadeouts"].InnerText);
                }

                //if (element["RemoveFadeins"] != null)
                //{
                //  chkRemoveFadeIns.Checked = Convert.ToBoolean(element["RemoveFadeins"].InnerText);
                //}
                chkRemoveFadeIns.Checked = chkRemoveTransitions.Checked;

                if (element["SaveDetectionLog"] != null)
                {
                    chkSaveDetectionLog.Checked = Convert.ToBoolean(element["SaveDetectionLog"].InnerText);
                }

                if (element["AutoSplitGames"] != null)
                {
                    var auto_split_element = element["AutoSplitGames"];

                    foreach (XmlElement game in auto_split_element)
                    {
                        if (game.LocalName != autoSplitData.GameName)
                        {
                            AllGameAutoSplitSettings[game.LocalName] = game;
                        }
                    }

                    if (auto_split_element[autoSplitData.GameName + autoSplitData.Category] != null)
                    {
                        var game_element = auto_split_element[autoSplitData.GameName + autoSplitData.Category];
                        AllGameAutoSplitSettings[autoSplitData.GameName + autoSplitData.Category] = game_element;
                        //var splits_element = game_element[autoSplitData.Category];
                        Dictionary<string, int> usedSplitNames = new Dictionary<string, int>();
                        foreach (XmlElement number_of_loads in game_element)
                        {
                            var up_down_controls = tabPage2.Controls.Find(number_of_loads.LocalName, true);

                            //This can happen if the layout was not saved and contains old splits.
                            if (up_down_controls == null || up_down_controls.Length == 0)
                            {
                                continue;
                            }

                            if (usedSplitNames.ContainsKey(number_of_loads.LocalName) == false)
                            {
                                usedSplitNames[number_of_loads.LocalName] = 0;
                            }
                            else
                            {
                                usedSplitNames[number_of_loads.LocalName]++;
                            }

                            //var up_down = tabPage2.Controls.Find(number_of_loads.LocalName, true).FirstOrDefault() as NumericUpDown;

                            NumericUpDown up_down = (NumericUpDown)up_down_controls[usedSplitNames[number_of_loads.LocalName]];

                            if (up_down != null)
                            {
                                up_down.Value = Convert.ToInt32(number_of_loads.InnerText);
                            }
                        }
                    }
                }

                DrawPreview();

                CaptureImageFullPreview(ref imageCaptureInfo, true);
            }
        }

        #endregion Public Methods

        #region Private Methods

        private void AutoSplitUpDown_ValueChanged(object sender, string splitName)
        {
            foreach (AutoSplitEntry entry in autoSplitData.SplitData)
            {
                if (entry.SplitName == splitName)
                {
                    entry.NumberOfLoads = (int)((NumericUpDown)sender).Value;
                    return;
                }
            }
        }

        private void CheckAutoReset_CheckedChanged(object sender, EventArgs e)
        {
        }

        private void CheckedListBox1_ItemCheck(object sender, ItemCheckEventArgs e)
        {
        }

        private void ComboBox1_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (processListComboBox.SelectedIndex < numScreens)
            {
                processCaptureIndex = -processListComboBox.SelectedIndex - 1;
            }
            else
            {
                processCaptureIndex = processListComboBox.SelectedIndex - numScreens;
            }

            selectionRectanglePreviewBox = new Rectangle(selectionTopLeft.X, selectionTopLeft.Y, selectionBottomRight.X - selectionTopLeft.X, selectionBottomRight.Y - selectionTopLeft.Y);

            DrawPreview();
        }

        private void CreateAutoSplitControls(LiveSplitState state)
        {
            autoSplitCategoryLbl.Text = "Category: " + state.Run.CategoryName;
            autoSplitNameLbl.Text = "Game: " + state.Run.GameName;

            int splitOffsetY = 95;
            int splitSpacing = 50;

            int splitCounter = 0;
            autoSplitData = new AutoSplitData(RemoveInvalidXMLCharacters(state.Run.GameName), RemoveInvalidXMLCharacters(state.Run.CategoryName));

            foreach (var split in state.Run)
            {
                //Setup controls for changing AutoSplit settings
                var autoSplitPanel = new Panel();
                var autoSplitLbl = new Label();
                var autoSplitUpDown = new NumericUpDown
                {
                    Value = 2
                };
                autoSplitPanel.BorderStyle = System.Windows.Forms.BorderStyle.Fixed3D;
                autoSplitPanel.Controls.Add(autoSplitUpDown);
                autoSplitPanel.Controls.Add(autoSplitLbl);
                autoSplitPanel.Location = new System.Drawing.Point(28, splitOffsetY + splitSpacing * splitCounter);
                autoSplitPanel.Size = new System.Drawing.Size(409, 39);

                autoSplitLbl.AutoSize = true;
                autoSplitLbl.Location = new System.Drawing.Point(3, 10);
                autoSplitLbl.Size = new System.Drawing.Size(199, 13);
                autoSplitLbl.TabIndex = 0;
                autoSplitLbl.Text = split.Name;

                autoSplitUpDown.Location = new System.Drawing.Point(367, 8);
                autoSplitUpDown.Size = new System.Drawing.Size(35, 20);
                autoSplitUpDown.TabIndex = 1;

                //Remove all whitespace to name the control, we can then access it in SetSettings.
                autoSplitUpDown.Name = RemoveInvalidXMLCharacters(split.Name);

                autoSplitUpDown.ValueChanged += (s, e) => AutoSplitUpDown_ValueChanged(autoSplitUpDown, RemoveInvalidXMLCharacters(split.Name));

                tabPage2.Controls.Add(autoSplitPanel);
                //tabPage2.Controls.Add(autoSplitLbl);
                //tabPage2.Controls.Add(autoSplitUpDown);

                autoSplitData.SplitData.Add(new AutoSplitEntry(RemoveInvalidXMLCharacters(split.Name), 2));
                dynamicAutoSplitterControls.Add(autoSplitPanel);
                splitCounter++;
            }
        }

        private void DrawCaptureRectangleBitmap()
        {
            Bitmap capture_image = (Bitmap)previewImage.Clone();
            //Draw selection rectangle
            using (Graphics g = Graphics.FromImage(capture_image))
            {
                Pen drawing_pen = new Pen(Color.Magenta, 8.0f)
                {
                    Alignment = PenAlignment.Inset
                };
                g.DrawRectangle(drawing_pen, selectionRectanglePreviewBox);
            }

            previewPictureBox.Image = capture_image;
        }

        private void DrawPreview()
        {
            try
            {
                ImageCaptureInfo copy = imageCaptureInfo;
                copy.CaptureSizeX = previewPictureBox.Width;
                copy.CaptureSizeY = previewPictureBox.Height;

                //Show something in the preview
                previewImage = CaptureImageFullPreview(ref copy);
                float crop_size_x = copy.ActualCropSizeX;
                float crop_size_y = copy.ActualCropSizeY;

                lastFullCapture = previewImage;
                //Draw selection rectangle
                DrawCaptureRectangleBitmap();

                //Compute image crop coordinates according to selection rectangle

                //Get raw image size from imageCaptureInfo.actual_crop_size to compute scaling between raw and rectangle coordinates

                //Console.WriteLine("SIZE X: {0}, SIZE Y: {1}", imageCaptureInfo.actual_crop_size_x, imageCaptureInfo.actual_crop_size_y);

                imageCaptureInfo.CropCoordinateLeft = selectionRectanglePreviewBox.Left * (crop_size_x / previewPictureBox.Width);
                imageCaptureInfo.CropCoordinateRight = selectionRectanglePreviewBox.Right * (crop_size_x / previewPictureBox.Width);
                imageCaptureInfo.CropCoordinateTop = selectionRectanglePreviewBox.Top * (crop_size_y / previewPictureBox.Height);
                imageCaptureInfo.CropCoordinateBottom = selectionRectanglePreviewBox.Bottom * (crop_size_y / previewPictureBox.Height);

                copy.CropCoordinateLeft = selectionRectanglePreviewBox.Left * (crop_size_x / previewPictureBox.Width);
                copy.CropCoordinateRight = selectionRectanglePreviewBox.Right * (crop_size_x / previewPictureBox.Width);
                copy.CropCoordinateTop = selectionRectanglePreviewBox.Top * (crop_size_y / previewPictureBox.Height);
                copy.CropCoordinateBottom = selectionRectanglePreviewBox.Bottom * (crop_size_y / previewPictureBox.Height);

                Bitmap full_cropped_capture = CaptureImageFullPreview(ref copy, useCrop: true);
                croppedPreviewPictureBox.Image = full_cropped_capture;
                lastFullCroppedCapture = full_cropped_capture;

                copy.CaptureSizeX = captureSize.Width;
                copy.CaptureSizeY = captureSize.Height;

                //Show matching bins for preview
                var capture = CaptureImage();
                var features = FeatureDetector.FeaturesFromBitmap(capture, out List<int> dummy, out int black_level, out List<int> dummy2);
                var isLoading = FeatureDetector.CompareFeatureVector(features.ToArray(), FeatureDetector.listOfFeatureVectorsEng, out int tempMatchingBins, -1.0f);

                lastFeatures = features;
                lastDiagnosticCapture = capture;
                lastMatchingBins = tempMatchingBins;
                matchDisplayLabel.Text = Math.Round((Convert.ToSingle(tempMatchingBins) / Convert.ToSingle(FeatureDetector.listOfFeatureVectorsEng.GetLength(1))), 4).ToString();
            }
            catch (Exception ex)
            {
                Console.WriteLine("Error: " + ex.ToString());
            }
        }

        private void EnableAutoSplitterChk_CheckedChanged(object sender, EventArgs e)
        {
            AutoSplitterEnabled = enableAutoSplitterChk.Checked;
        }

        private void InitImageCaptureInfo()
        {
            imageCaptureInfo = new ImageCaptureInfo();

            selectionTopLeft = new Point(0, 0);
            selectionBottomRight = new Point(previewPictureBox.Width, previewPictureBox.Height);
            selectionRectanglePreviewBox = new Rectangle(selectionTopLeft.X, selectionTopLeft.Y, selectionBottomRight.X - selectionTopLeft.X, selectionBottomRight.Y - selectionTopLeft.Y);

            imageCaptureInfo.FeatureVectorResolutionX = featureVectorResolutionX;
            imageCaptureInfo.FeatureVectorResolutionY = featureVectorResolutionY;
            imageCaptureInfo.CaptureSizeX = captureSize.Width;
            imageCaptureInfo.CaptureSizeY = captureSize.Height;
            imageCaptureInfo.CropOffsetX = cropOffsetX;
            imageCaptureInfo.CropOffsetY = cropOffsetY;
            imageCaptureInfo.CaptureAspectRatio = captureAspectRatioX / captureAspectRatioY;
        }

        private void PreviewPictureBox_MouseClick(object sender, MouseEventArgs e)
        {
        }

        private void PreviewPictureBox_MouseDown(object sender, MouseEventArgs e)
        {
            SetRectangleFromMouse(e);
            DrawPreview();
        }

        private void PreviewPictureBox_MouseMove(object sender, MouseEventArgs e)
        {
            SetRectangleFromMouse(e);
            if (drawingPreview == false)
            {
                drawingPreview = true;
                //Draw selection rectangle
                DrawCaptureRectangleBitmap();
                drawingPreview = false;
            }
        }

        private void PreviewPictureBox_MouseUp(object sender, MouseEventArgs e)
        {
            SetRectangleFromMouse(e);
            DrawPreview();
        }

        private void ProcessListComboBox_DropDown(object sender, EventArgs e)
        {
            RefreshCaptureWindowList();
        }

        private void RefreshCaptureWindowList()
        {
            try
            {
                Process[] processListtmp = Process.GetProcesses();
                List<Process> processes_with_name = new List<Process>();

                if (captureIDs != null)
                {
                    if (processListComboBox.SelectedIndex < captureIDs.Count && processListComboBox.SelectedIndex >= 0)
                    {
                        selectedCaptureID = processListComboBox.SelectedItem.ToString();
                    }
                }

                captureIDs = new List<string>();

                processListComboBox.Items.Clear();
                numScreens = 0;
                foreach (var screen in Screen.AllScreens)
                {
                    // For each screen, add the screen properties to a list box.
                    processListComboBox.Items.Add("Screen: " + screen.DeviceName + ", " + screen.Bounds.ToString());
                    captureIDs.Add("Screen: " + screen.DeviceName);
                    numScreens++;
                }
                foreach (Process process in processListtmp)
                {
                    if (!String.IsNullOrEmpty(process.MainWindowTitle))
                    {
                        //Console.WriteLine("Process: {0} ID: {1} Window title: {2} HWND PTR {3}", process.ProcessName, process.Id, process.MainWindowTitle, process.MainWindowHandle);
                        processListComboBox.Items.Add(process.ProcessName + ": " + process.MainWindowTitle);
                        captureIDs.Add(process.ProcessName);
                        processes_with_name.Add(process);
                    }
                }

                UpdateIndexToCaptureID();

                //processListComboBox.SelectedIndex = 0;
                processList = processes_with_name.ToArray();
            }
            catch (Exception ex)
            {
                Console.WriteLine("Error: " + ex.ToString());
            }
        }

        public string RemoveInvalidXMLCharacters(string in_string)
        {
            if (in_string == null) return null;

            StringBuilder sbOutput = new StringBuilder();
            char ch;

            bool was_other_char = false;

            for (int i = 0; i < in_string.Length; i++)
            {
                ch = in_string[i];

                if ((ch >= 0x0 && ch <= 0x2F) ||
                    (ch >= 0x3A && ch <= 0x40) ||
                    (ch >= 0x5B && ch <= 0x60) ||
                    (ch >= 0x7B)
                    )
                {
                    continue;
                }

                //Can't start with a number.
                if (was_other_char == false && ch >= '0' && ch <= '9')
                {
                    continue;
                }

                /*if ((ch >= 0x0020 && ch <= 0xD7FF) ||
					(ch >= 0xE000 && ch <= 0xFFFD) ||
					ch == 0x0009 ||
					ch == 0x000A ||
					ch == 0x000D)
				{*/
                sbOutput.Append(ch);
                was_other_char = true;
                //}
            }

            if (sbOutput.Length == 0)
            {
                sbOutput.Append("NULL");
            }

            return sbOutput.ToString();
        }

        private void RequiredMatchesUpDown_ValueChanged(object sender, EventArgs e)
        {
            MinimumBlackLength = Convert.ToSingle(requiredMatchesUpDown.Value);
        }

        private void SaveDiagnosticsButton_Click(object sender, EventArgs e)
        {
            try
            {
                FolderBrowserDialog fbd = new FolderBrowserDialog();

                var result = fbd.ShowDialog();

                if (result != DialogResult.OK)
                {
                    return;
                }

                //System.IO.Directory.CreateDirectory(fbd.SelectedPath);
                numCaptures++;
                lastFullCapture.Save(fbd.SelectedPath + "/" + numCaptures.ToString() + "_FULL_" + lastMatchingBins + ".jpg", ImageFormat.Jpeg);
                lastFullCroppedCapture.Save(fbd.SelectedPath + "/" + numCaptures.ToString() + "_FULL_CROPPED_" + lastMatchingBins + ".jpg", ImageFormat.Jpeg);
                lastDiagnosticCapture.Save(fbd.SelectedPath + "/" + numCaptures.ToString() + "_DIAGNOSTIC_" + lastMatchingBins + ".jpg", ImageFormat.Jpeg);
                SaveFeatureVectorToTxt(lastFeatures, numCaptures.ToString() + "_FEATURES_" + lastMatchingBins + ".txt", fbd.SelectedPath);
            }
            catch (Exception ex)
            {
                Console.WriteLine("Error: " + ex.ToString());
            }
        }

        private void SaveFeatureVectorToTxt(List<int> featureVector, string filename, string directoryName)
        {
            System.IO.Directory.CreateDirectory(directoryName);
            try
            {
                using (var file = File.CreateText(directoryName + "/" + filename))
                {
                    file.Write("{");
                    file.Write(string.Join(",", featureVector));
                    file.Write("},\n");
                }
            }
            catch
            {
                //yeah, silent catch is bad, I don't care
            }
        }

        private void SetRectangleFromMouse(MouseEventArgs e)
        {
            //Clamp values to pictureBox range
            int x = Math.Min(Math.Max(0, e.Location.X), previewPictureBox.Width);
            int y = Math.Min(Math.Max(0, e.Location.Y), previewPictureBox.Height);

            if (e.Button == MouseButtons.Left
                && (selectionRectanglePreviewBox.Left + selectionRectanglePreviewBox.Width) - x > 0
                && (selectionRectanglePreviewBox.Top + selectionRectanglePreviewBox.Height) - y > 0)
            {
                selectionTopLeft = new Point(x, y);
            }
            else if (e.Button == MouseButtons.Right && x - selectionRectanglePreviewBox.Left > 0 && y - selectionRectanglePreviewBox.Top > 0)
            {
                selectionBottomRight = new Point(x, y);
            }

            do_not_trigger_value_changed = true;
            numTopLeftRectY.Value = selectionTopLeft.Y;

            do_not_trigger_value_changed = true;
            numTopLeftRectX.Value = selectionTopLeft.X;

            do_not_trigger_value_changed = true;
            numBottomRightRectY.Value = selectionBottomRight.Y;

            do_not_trigger_value_changed = true;
            numBottomRightRectX.Value = selectionBottomRight.X;

            selectionRectanglePreviewBox = new Rectangle(selectionTopLeft.X, selectionTopLeft.Y, selectionBottomRight.X - selectionTopLeft.X, selectionBottomRight.Y - selectionTopLeft.Y);
        }

        private XmlElement ToElement<T>(XmlDocument document, String name, T value)
        {
            var element = document.CreateElement(name);
            element.InnerText = value.ToString();
            return element;
        }

        private void TrackBar1_ValueChanged(object sender, EventArgs e)
        {
            scalingValue = trackBar1.Value;

            if (scalingValue % trackBar1.SmallChange != 0)
            {
                scalingValue = (scalingValue / trackBar1.SmallChange) * trackBar1.SmallChange;

                trackBar1.Value = scalingValue;
            }

            scalingValueFloat = ((float)scalingValue) / 100.0f;

            scalingLabel.Text = "Scaling: " + trackBar1.Value.ToString() + "%";

            DrawPreview();
        }

        private void UpdateIndexToCaptureID()
        {
            //Find matching window, set selected index to index in dropdown items
            for (int item_index = 0; item_index < processListComboBox.Items.Count; item_index++)
            {
                String item = processListComboBox.Items[item_index].ToString();
                if (item.Contains(selectedCaptureID))
                {
                    processListComboBox.SelectedIndex = item_index;
                    //processListComboBox.Text = processListComboBox.SelectedItem.ToString();

                    break;
                }
            }
        }

        private void UpdatePreviewButton_Click(object sender, EventArgs e)
        {
            DrawPreview();
        }

        #endregion Private Methods

        private void ChkAutoSplitterDisableOnSkip_CheckedChanged(object sender, EventArgs e)
        {
            AutoSplitterDisableOnSkipUntilSplit = chkAutoSplitterDisableOnSkip.Checked;
        }

        private void ChkRemoveTransitions_CheckedChanged(object sender, EventArgs e)
        {
            RemoveFadeouts = chkRemoveTransitions.Checked;
            RemoveFadeins = chkRemoveTransitions.Checked;
        }

        private void ChkSaveDetectionLog_CheckedChanged(object sender, EventArgs e)
        {
            SaveDetectionLog = chkSaveDetectionLog.Checked;
        }

        private void ChkRemoveFadeIns_CheckedChanged(object sender, EventArgs e)
        {
            //RemoveFadeins = chkRemoveFadeIns.Checked;
            RemoveFadeins = chkRemoveTransitions.Checked;
        }

        private bool IsFeatureUnique(DetectorData data, int[] feature)
        {
            return !FeatureDetector.CompareFeatureVector(feature, data.features, out _, -1.0f);
        }

        private void ComputeDatabaseFromPath(string path)
        {
            DetectorData data = new DetectorData
            {
                sizeX = captureSize.Width,
                sizeY = captureSize.Height,
                numberOfHistogramBins = 16,
                numPatchesX = 6,
                numPatchesY = 2,
                offsetX = Convert.ToInt32(cropOffsetX),
                offsetY = Convert.ToInt32(cropOffsetY),
                features = new List<int[]>()
            };

            var files = System.IO.Directory.GetFiles(path);

            float[] downsampling_factors = { 1, 2, 3 };
            float[] brightness_values = { 1.0f, 0.97f, 1.03f };
            float[] contrast_values = { 1.0f, 0.97f, 1.03f };
            InterpolationMode[] interpolation_modes = { InterpolationMode.NearestNeighbor, InterpolationMode.Bicubic };

            int previous_matching_bins = FeatureDetector.numberOfBinsCorrect;
            FeatureDetector.numberOfBinsCorrect = 420;

            foreach (string filename in files)
            {
                foreach (float downsampling_factor in downsampling_factors)
                {
                    foreach (float brightness in brightness_values)
                    {
                        foreach (float contrast in contrast_values)
                        {
                            foreach (InterpolationMode interpolation_mode in interpolation_modes)
                            {
                                float gamma = 1.0f; // no change in gamma

                                float adjustedBrightness = brightness - 1.0f;
                                // create matrix that will brighten and contrast the image
                                // https://stackoverflow.com/a/15408608
                                float[][] ptsArray = {
                  new float[] {contrast, 0, 0, 0, 0}, // scale red
                  new float[] {0, contrast, 0, 0, 0}, // scale green
                  new float[] {0, 0, contrast, 0, 0}, // scale blue
                  new float[] {0, 0, 0, 1.0f, 0}, // don't scale alpha
                  new float[] {adjustedBrightness, adjustedBrightness, adjustedBrightness, 0, 1}};

                                Bitmap bmp = new Bitmap(filename);

                                //Make 32 bit ARGB bitmap
                                Bitmap clone = new Bitmap(bmp.Width, bmp.Height,
                                  System.Drawing.Imaging.PixelFormat.Format32bppArgb);

                                //Make 32 bit ARGB bitmap
                                Bitmap sample_factor_clone = new Bitmap(Convert.ToInt32(bmp.Width / downsampling_factor), Convert.ToInt32(bmp.Height / downsampling_factor),
                                  System.Drawing.Imaging.PixelFormat.Format32bppArgb);

                                var attributes = new ImageAttributes();
                                attributes.SetWrapMode(WrapMode.TileFlipXY);

                                using (Graphics gr = Graphics.FromImage(sample_factor_clone))
                                {
                                    gr.InterpolationMode = interpolation_mode;
                                    gr.DrawImage(bmp, new Rectangle(0, 0, sample_factor_clone.Width, sample_factor_clone.Height), 0, 0, bmp.Width, bmp.Height, GraphicsUnit.Pixel, attributes);
                                }

                                attributes.ClearColorMatrix();
                                attributes.SetColorMatrix(new ColorMatrix(ptsArray), ColorMatrixFlag.Default, ColorAdjustType.Bitmap);
                                attributes.SetGamma(gamma, ColorAdjustType.Bitmap);

                                using (Graphics gr = Graphics.FromImage(clone))
                                {
                                    gr.InterpolationMode = interpolation_mode;
                                    gr.DrawImage(sample_factor_clone, new Rectangle(0, 0, clone.Width, clone.Height), 0, 0, sample_factor_clone.Width, sample_factor_clone.Height, GraphicsUnit.Pixel, attributes);
                                }

                                List<int> max_per_patch = new List<int>();
                                List<int> min_per_patch = new List<int>();
                                int[] feature = FeatureDetector.FeaturesFromBitmap(clone, out max_per_patch, out int black_level, out min_per_patch).ToArray();

                                if (IsFeatureUnique(data, feature))
                                {
                                    data.features.Add(feature);
                                }

                                bmp.Dispose();
                                clone.Dispose();
                                sample_factor_clone.Dispose();
                            }
                        }
                    }
                }
            }

            SerializeDetectorData(data, new DirectoryInfo(path).Name);
            FeatureDetector.numberOfBinsCorrect = previous_matching_bins;
        }

        private void DevToolsDatabaseButton_Click(object sender, EventArgs e)
        {
            using (var fbd = new System.Windows.Forms.FolderBrowserDialog())
            {
                DialogResult result = fbd.ShowDialog();

                if (result != DialogResult.OK)
                {
                    return;
                }

                ComputeDatabaseFromPath(fbd.SelectedPath);
            }
        }

        private void SerializeDetectorData(DetectorData data, string path_suffix = "")
        {
            IFormatter formatter = new BinaryFormatter();
            System.IO.Directory.CreateDirectory(Path.Combine(DetectionLogFolderName, "SerializedData"));
            Stream stream = new FileStream(Path.Combine(DetectionLogFolderName, "SerializedData", "LiveSplit.BlackScreenDetector_" + DateTime.Now.ToString("yyyy_MM_dd_HH_mm_ss_fff") + "_" + path_suffix + ".ctrnfdata"), FileMode.Create, FileAccess.Write);

            formatter.Serialize(stream, data);
            stream.Close();
        }

        private void TrackBar1_Scroll(object sender, EventArgs e)
        {
        }

        private bool do_not_trigger_value_changed = false;

        private void NumericUpDown4_ValueChanged(object sender, EventArgs e)
        {
            if (do_not_trigger_value_changed == false)
            {
                SetRectangleFromMouse(new MouseEventArgs(MouseButtons.Left, 1, Convert.ToInt32(numTopLeftRectX.Value), Convert.ToInt32(numTopLeftRectY.Value), 0));
                DrawPreview();
            }
            do_not_trigger_value_changed = false;
        }

        private void NumericUpDown3_ValueChanged(object sender, EventArgs e)
        {
            if (do_not_trigger_value_changed == false)
            {
                SetRectangleFromMouse(new MouseEventArgs(MouseButtons.Left, 1, Convert.ToInt32(numTopLeftRectX.Value), Convert.ToInt32(numTopLeftRectY.Value), 0));
                DrawPreview();
            }
            do_not_trigger_value_changed = false;
        }

        private void NumericUpDown2_ValueChanged(object sender, EventArgs e)
        {
            if (do_not_trigger_value_changed == false)
            {
                SetRectangleFromMouse(new MouseEventArgs(MouseButtons.Right, 1, Convert.ToInt32(numBottomRightRectX.Value), Convert.ToInt32(numBottomRightRectY.Value), 0));
                DrawPreview();
            }
            do_not_trigger_value_changed = false;
        }

        private void NumericUpDown1_ValueChanged(object sender, EventArgs e)
        {
            if (do_not_trigger_value_changed == false)
            {
                SetRectangleFromMouse(new MouseEventArgs(MouseButtons.Right, 1, Convert.ToInt32(numBottomRightRectX.Value), Convert.ToInt32(numBottomRightRectY.Value), 0));
                DrawPreview();
            }

            do_not_trigger_value_changed = false;
        }

        private void BlackLevelNumericUpDown_ValueChanged(object sender, EventArgs e)
        {
            BlackLevel = Convert.ToInt32(blackLevelNumericUpDown.Value);
        }
    }

    [Serializable]
    public class DetectorData
    {
        public int offsetX;
        public int offsetY;
        public int sizeX;
        public int sizeY;
        public int numPatchesX;
        public int numPatchesY;
        public int numberOfHistogramBins;

        public List<int[]> features;
    }

    public class AutoSplitData
    {
        #region Public Fields

        public string Category;
        public string GameName;
        public List<AutoSplitEntry> SplitData;

        #endregion Public Fields

        #region Public Constructors

        public AutoSplitData(string gameName, string category)
        {
            SplitData = new List<AutoSplitEntry>();
            GameName = gameName;
            Category = category;
        }

        #endregion Public Constructors
    }

    public class AutoSplitEntry
    {
        #region Public Fields

        public int NumberOfLoads = 2;
        public string SplitName = "";

        #endregion Public Fields

        #region Public Constructors

        public AutoSplitEntry(string splitName, int numberOfLoads)
        {
            SplitName = splitName;
            NumberOfLoads = numberOfLoads;
        }

        #endregion Public Constructors
    }
}