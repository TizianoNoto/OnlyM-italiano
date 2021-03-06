﻿namespace OnlyM.Services.DragAndDrop
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using System.Linq;
    using System.Threading.Tasks;
    using System.Windows;
    using Core.Services.Media;
    using Core.Services.Options;
    using Models;
    using OnlyM.Slides;
    using Serilog;
    using Snackbar;

    // ReSharper disable once ClassNeverInstantiated.Global
    internal sealed class DragAndDropService : IDragAndDropService
    {
        private readonly IMediaProviderService _mediaProviderService;
        private readonly IOptionsService _optionsService;
        private readonly ISnackbarService _snackbarService;
        private bool _canDrop;

        public DragAndDropService(
            IMediaProviderService mediaProviderService,
            IOptionsService optionsService,
            ISnackbarService snackbarService)
        {
            _mediaProviderService = mediaProviderService;
            _optionsService = optionsService;
            _snackbarService = snackbarService;
        }

        public event EventHandler<FilesCopyProgressEventArgs> CopyingFilesProgressEvent;

        public void Init(FrameworkElement targetElement)
        {
            targetElement.DragEnter += HandleDragEnter;
            targetElement.DragOver += HandleDragOver;
            targetElement.Drop += HandleDrop;
        }

        private void HandleDragOver(object sender, DragEventArgs e)
        {
            SetEffects(e);
            e.Handled = true;
        }

        private void HandleDrop(object sender, DragEventArgs e)
        {
            CopyMediaFiles(e.Data);
        }

        private void HandleDragEnter(object sender, DragEventArgs e)
        {
            // do we allow drop of drag object?
            _canDrop = CanDropOrPaste(e.Data);
            SetEffects(e);
            e.Handled = true;
        }

        private void SetEffects(DragEventArgs e)
        {
            e.Effects = _canDrop ? DragDropEffects.Copy : DragDropEffects.None;
        }

        private void CopyMediaFiles(IDataObject data)
        {
            if (data != null)
            {
                Task.Run(() =>
                {
                    int count = InternalCopyMediaFiles(data, out var someError);
                    DisplaySnackbar(count, someError);
                });
            }
        }

        private void DisplaySnackbar(int count, bool someError)
        {
            if (someError)
            {
                _snackbarService.EnqueueWithOk(Properties.Resources.COPYING_ERROR);
            }
            else if (count == 0)
            {
                _snackbarService.EnqueueWithOk(Properties.Resources.NO_SUPPORTED_FILES);
            }
            else if (count == 1)
            {
                _snackbarService.EnqueueWithOk(Properties.Resources.FILE_COPIED);
            }
            else
            {
                _snackbarService.EnqueueWithOk(string.Format(Properties.Resources.FILES_COPIED, count));
            }
        }

        private int InternalCopyMediaFiles(IDataObject data, out bool someError)
        {
            var count = 0;
            someError = false;
            
            try
            {
                var mediaFolder = _optionsService.Options.MediaFolder;

                var files = GetSupportedFiles(data).ToArray();
                if (!files.Any())
                {
                    return 0;
                }

                bool shouldCreateSlideshow = DataIsFromOnlyV(data) && files.Length > 1;

                count = shouldCreateSlideshow 
                    ? CopyAsSlideshow(mediaFolder, data, files) 
                    : CopyAsIndividualFiles(mediaFolder, files);
            }
            catch (Exception ex)
            {
                Log.Logger.Error(ex, "Could not copy media files");
                someError = true;
            }
            finally
            {
                OnCopyingFilesProgressEvent(new FilesCopyProgressEventArgs
                {
                    Status = FileCopyStatus.FinishedCopy
                });
            }

            return count;
        }

        private int CopyAsSlideshow(string mediaFolder, IDataObject data, string[] files)
        {
            var title = GetOnlyVTitle(data);
            if (!string.IsNullOrEmpty(title))
            {
                var sfb = new SlideFileBuilder { AutoPlay = false, Loop = false };

                for (int n = 0; n < files.Length; ++n)
                {
                    var file = files[n];
                    sfb.AddSlide(file, n == 0, false, n == file.Length - 1, false);
                }

                var destFilename = Path.Combine(mediaFolder, title + ".omslide");
                sfb.Build(destFilename, overwrite: true);
                
                return 1;
            }

            return 0;
        }

        private int CopyAsIndividualFiles(string mediaFolder, string[] files)
        {
            int count = 0;

            foreach (var file in files)
            {
                var filename = Path.GetFileName(file);

                if (!string.IsNullOrEmpty(filename))
                {
                    var destFile = Path.Combine(mediaFolder, filename);
                    if (!File.Exists(destFile))
                    {
                        OnCopyingFilesProgressEvent(new FilesCopyProgressEventArgs
                        {
                            FilePath = destFile,
                            Status = FileCopyStatus.StartingCopy
                        });

                        File.Copy(file, destFile, false);
                        ++count;

                        OnCopyingFilesProgressEvent(new FilesCopyProgressEventArgs
                        {
                            FilePath = destFile,
                            Status = FileCopyStatus.FinishedCopy
                        });
                    }
                }
            }

            return count;
        }

        private bool CanDropOrPaste(IDataObject data)
        {
            return GetSupportedFiles(data).Any();
        }

        private bool DataIsFromOnlyV(IDataObject data)
        {
            if (data.GetDataPresent(DataFormats.StringFormat))
            {
                var s = (string)data.GetData(DataFormats.StringFormat);
                if (s != null && s.StartsWith("OnlyV|", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private string GetOnlyVTitle(IDataObject data)
        {
            var s = (string)data.GetData(DataFormats.StringFormat);
            return s?.Split('|')[1];
        }

        private IEnumerable<string> GetSupportedFiles(IDataObject data)
        {
            var result = new List<string>();

            if (data.GetDataPresent(DataFormats.FileDrop))
            {
                // Note that you can have more than one file...
                string[] files = (string[])data.GetData(DataFormats.FileDrop);

                if (files == null || !files.Any())
                {
                    return result;
                }
                
                foreach (var file in files)
                {
                    if (Directory.Exists(file))
                    {
                        // a folder rather than a file.
                        foreach (var fileInFolder in Directory.GetFiles(file))
                        {
                            var fileToAdd = GetSupportedFile(fileInFolder);
                            if (fileToAdd != null)
                            {
                                result.Add(fileToAdd);
                            }
                        }
                    }
                    else
                    {
                        var fileToAdd = GetSupportedFile(file);
                        if (fileToAdd != null)
                        {
                            result.Add(fileToAdd);
                        }
                    }
                }
            }

            Log.Logger.Verbose($"Found {result.Count} supported files in drag-and-drop operation");

            result.Sort();

            return result;
        }

        private string GetSupportedFile(string file)
        {
            var ext = Path.GetExtension(file);
            if (string.IsNullOrEmpty(ext) || !_mediaProviderService.IsFileExtensionSupported(ext))
            {
                return null;
            }

            return file;
        }

        private void OnCopyingFilesProgressEvent(FilesCopyProgressEventArgs e)
        {
            CopyingFilesProgressEvent?.Invoke(this, e);
        }
    }
}
