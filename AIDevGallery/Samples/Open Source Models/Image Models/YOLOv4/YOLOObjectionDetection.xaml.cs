// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using AIDevGallery.Models;
using AIDevGallery.Samples.Attributes;
using AIDevGallery.Samples.SharedCode;
using AIDevGallery.Utils;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Windows.Storage.Pickers;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace AIDevGallery.Samples.OpenSourceModels.YOLOv4
{
    [GallerySample(
        Model1Types = [ModelType.YOLO],
        Scenario = ScenarioType.ImageDetectObjects,
        SharedCode = [
            SharedCodeEnum.Prediction,
            SharedCodeEnum.BitmapFunctions,
            SharedCodeEnum.RCNNLabelMap,
            SharedCodeEnum.YOLOHelpers
        ],
        NugetPackageReferences = [
            "System.Drawing.Common",
            "Microsoft.ML.OnnxRuntime.DirectML",
            "Microsoft.ML.OnnxRuntime.Extensions"
        ],
        Name = "Object Detection",
        Id = "9b74ccc0-f5f7-430f-bed0-758ffd163508",
        Icon = "\uE8B3")]

    internal sealed partial class YOLOObjectionDetection : Page
    {
        private InferenceSession? _inferenceSession;

        public YOLOObjectionDetection()
        {
            this.Unloaded += (s, e) => _inferenceSession?.Dispose();

            this.Loaded += (s, e) => Page_Loaded();
            this.InitializeComponent();
        }

        private void Page_Loaded()
        {
            UploadButton.Focus(FocusState.Programmatic);
        }

        /// <inheritdoc/>
        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            if (e.Parameter is SampleNavigationParameters sampleParams)
            {
                var hardwareAccelerator = sampleParams.HardwareAccelerator;
                await InitModel(sampleParams.ModelPath, hardwareAccelerator);
                sampleParams.NotifyCompletion();

                // Loads inference on default image
                await DetectObjects(Windows.ApplicationModel.Package.Current.InstalledLocation.Path + "\\Assets\\team.jpg");
            }
        }

        private Task InitModel(string modelPath, HardwareAccelerator hardwareAccelerator)
        {
            return Task.Run(() =>
            {
                if (_inferenceSession != null)
                {
                    return;
                }

                SessionOptions sessionOptions = new();
                sessionOptions.RegisterOrtExtensions();
                if (hardwareAccelerator == HardwareAccelerator.DML)
                {
                    sessionOptions.AppendExecutionProvider_DML(DeviceUtils.GetBestDeviceId());
                }

                _inferenceSession = new InferenceSession(modelPath, sessionOptions);
            });
        }

        private async void UploadButton_Click(object sender, RoutedEventArgs e)
        {
            var window = new Window();
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);

            // Create a FileOpenPicker
            var picker = new FileOpenPicker();

            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

            // Set the file type filter
            picker.FileTypeFilter.Add(".png");
            picker.FileTypeFilter.Add(".jpeg");
            picker.FileTypeFilter.Add(".jpg");
            picker.FileTypeFilter.Add(".bmp");

            picker.ViewMode = PickerViewMode.Thumbnail;

            // Pick a file
            var file = await picker.PickSingleFileAsync();
            if (file != null)
            {
                // Call function to run inference and classify image
                UploadButton.Focus(FocusState.Programmatic);
                await DetectObjects(file.Path);
            }
        }

        private async Task DetectObjects(string filePath)
        {
            if (_inferenceSession == null)
            {
                return;
            }

            Loader.IsActive = true;
            Loader.Visibility = Visibility.Visible;
            UploadButton.Visibility = Visibility.Collapsed;

            DefaultImage.Source = new BitmapImage(new Uri(filePath));
            NarratorHelper.AnnounceImageChanged(DefaultImage, "Image changed: new upload."); // <exclude-line>

            Bitmap image = new(filePath);

            int originalWidth = image.Width;
            int originalHeight = image.Height;

            var predictions = await Task.Run(() =>
            {
                // Set up
                var inputName = _inferenceSession.InputNames[0];
                var inputDimensions = _inferenceSession.InputMetadata[inputName].Dimensions;

                // Set batch size
                int batchSize = 1;
                inputDimensions[0] = batchSize;

                // I know the input dimensions to be [batchSize, 416, 416, 3]
                int inputWidth = inputDimensions[1];
                int inputHeight = inputDimensions[2];

                using var resizedImage = BitmapFunctions.ResizeWithPadding(image, inputWidth, inputHeight);

                // Preprocessing
                Tensor<float> input = new DenseTensor<float>(inputDimensions);
                input = BitmapFunctions.PreprocessBitmapForYOLO(resizedImage, input);

                // Setup inputs and outputs
                var inputMetadataName = _inferenceSession!.InputNames[0];
                var inputs = new List<NamedOnnxValue>
                {
                    NamedOnnxValue.CreateFromTensor(inputMetadataName, input)
                };

                // Run inference
                using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results = _inferenceSession!.Run(inputs);

                // Extract tensors from inference results
                var outputTensor1 = results[0].AsTensor<float>();
                var outputTensor2 = results[1].AsTensor<float>();
                var outputTensor3 = results[2].AsTensor<float>();

                // Define anchors (as per your model)
                var anchors = new List<(float Width, float Height)>
                {
                    (12, 16), (19, 36), (40, 28),   // Small grid (52x52)
                    (36, 75), (76, 55), (72, 146),  // Medium grid (26x26)
                    (142, 110), (192, 243), (459, 401) // Large grid (13x13)
                };

                // Combine tensors into a list for processing
                var gridTensors = new List<Tensor<float>> { outputTensor1, outputTensor2, outputTensor3 };

                // Postprocessing steps
                var extractedPredictions = YOLOHelpers.ExtractPredictions(gridTensors, anchors, inputWidth, inputHeight, originalWidth, originalHeight);
                var filteredPredictions = YOLOHelpers.ApplyNms(extractedPredictions, .4f);

                // Return the final predictions
                return filteredPredictions;
            });

            BitmapImage outputImage = BitmapFunctions.RenderPredictions(image, predictions);

            DispatcherQueue.TryEnqueue(() =>
            {
                DefaultImage.Source = outputImage;
                Loader.IsActive = false;
                Loader.Visibility = Visibility.Collapsed;
                UploadButton.Visibility = Visibility.Visible;
            });

            NarratorHelper.AnnounceImageChanged(DefaultImage, "Image changed: objects detected."); // <exclude-line>
            image.Dispose();
        }
    }
}