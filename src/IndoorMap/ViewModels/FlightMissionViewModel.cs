﻿using System;
using System.Collections.Generic;
using System.Linq;
using Extype;
using LiveChartsCore.SkiaSharpView.Avalonia;
using IndoorMap.Services.Abstract;
using IndoorMap.Views;
using IndoorMap.Models;
using DomainModel;
using NLog;
using Prism.Commands;
using Prism.Dialogs;
using System.IO;
using System.Diagnostics;
using Renci.SshNet;
using System.Text.RegularExpressions;


namespace IndoorMap.ViewModels
{
    public class FlightMissionViewModel : ViewModelBase
    {
        private readonly Logger _logger = LogManager.GetLogger(nameof(FlightMissionViewModel));
        
        private readonly INotificationService _notificationService;
        private readonly IDialogService _dialogService;
        private readonly IConfig _config;

        public ArucoMarkerLine[] ArucoMarkersSet1 { get; }

        public ArucoMarkerLine[] ArucoMarkersSet2 { get; }

        public ArucoMarkerLine[] ArucoMarkersSet3 { get; }

        /// <summary>
        /// Gets the command to add the point to the flight mission.
        /// </summary>
        public DelegateCommand<object> AddPointCommand { get; set; }
        
        /// <summary>
        /// Gets the command to remove the point from the flight mission.
        /// </summary>
        public DelegateCommand<object> RemovePointCommand { get; }
        
        /// <summary>
        /// Gets the command to clear the flight mission.
        /// </summary>
        public DelegateCommand ClearMissionCommand { get; }
        
        /// <summary>
        /// Gets the command to save the flight mission on the quad.
        /// </summary>
        public DelegateCommand SaveMissionCommand { get; }
        
        /// <summary>
        /// Gets the command to start the flight mission.
        /// </summary>
        public DelegateCommand StartMissionCommand { get; }

        public FlightMissionViewModel(
            INotificationService notificationService,
            IDialogService dialogService,
            IConfig config)
        {
            _notificationService = notificationService.ThrowIfNull(nameof(notificationService));
            _dialogService = dialogService.ThrowIfNull(nameof(dialogService));
            _config = config.ThrowIfNull(nameof(config));

            AddPointCommand = new DelegateCommand<object>(AddPoint);
            RemovePointCommand = new DelegateCommand<object>(RemovePoint);

            ClearMissionCommand = new DelegateCommand(ClearMission);
            SaveMissionCommand = new DelegateCommand(SaveMission);
            StartMissionCommand = new DelegateCommand(StartMission);

            ArucoMarkersSet1 = GetArucoMarkerSet(0, 29);
            ArucoMarkersSet2 = GetArucoMarkerSet(30, 59);
            ArucoMarkersSet3 = GetArucoMarkerSet(60, 89);

            EnsureFlightMissionFileExists();
        }

        private ArucoMarkerLine[] GetArucoMarkerSet(int from, int to)
        {
            var arucoSet = new List<ArucoMarkerLine>();
            var z = 2;

            for (int i = from + 2; i <= to; i += 3)
            {
                var marker1 = new ArucoMarker(i - 2, z, Guid.NewGuid().ToString());
                var marker2 = new ArucoMarker(i - 1, z, Guid.NewGuid().ToString());
                var marker3 = new ArucoMarker(i, z, Guid.NewGuid().ToString());

                var line = new ArucoMarkerLine(marker1, marker2, marker3);
                arucoSet.Add(line);
            }

            return arucoSet.ToArray();
        }
        
        public void AddPoint(object commandParam)
        {
            try
            {
                var arucoMarker = (ArucoMarker)commandParam;
                var dialogParameters = new DialogParameters
                {
                    { nameof(ArucoMarker), arucoMarker }
                };

                _dialogService.ShowDialog(nameof(PointDialogView), dialogParameters, HandleDialogResult());        
            }
            catch (Exception ex)
            {
                _notificationService.NotifyAboutErrorAsync("Что-то пошло не так. Проверьте логи.");
                _logger.Error(ex);
            }
        }

        private Action<IDialogResult> HandleDialogResult()
        {
            return r =>
            {
                switch (r.Result)
                {
                    case ButtonResult.OK:
                        var arucoMarker = r.Parameters[nameof(ArucoMarker)] as ArucoMarker;
                        _notificationService.NotifyAboutSuccess("Точка " + arucoMarker.Number + " добавлена");
                        AddPointToFile(arucoMarker);
                        break;
                }
            };
        }

        private void AddPointToFile(ArucoMarker arucoMarker)
        {
            var fileStream = new FileStream(_config.FlightMissionFile, FileMode.Append, FileAccess.Write);
            var steamWriter = new StreamWriter(fileStream, System.Text.Encoding.ASCII);

            try
            {
                var coords = $"{arucoMarker.Number},{arucoMarker.Altitude}";
                steamWriter.WriteLine(coords);
            }
            catch (Exception ex)
            {
                _notificationService.NotifyAboutFailure(ex.Message);
                _logger.Error(ex, ex.Message);
            }
            finally
            {
                steamWriter.Close();
                fileStream.Close();
            }
        }

        private void RemovePoint(object commandParam)
        {
        }

        private void ClearMission()
        {
            ClearMissionFile();
        }

        private void ClearMissionFile()
        {
            try
            {
                EnsureFlightMissionFileExists();

                File.WriteAllText(_config.FlightMissionFile, string.Empty);
                _notificationService.NotifyAboutSuccess("Полетное задание очищено");
            }
            catch (Exception ex)
            {
                _notificationService.NotifyAboutFailure("Доступ к файлу полетного задания ограничен");
                _logger.Error(ex, ex.Message);
            }
        }

        private void EnsureFlightMissionFileExists()
        {
            if (!File.Exists(_config.FlightMissionFile))
            {
                File.Create(_config.FlightMissionFile);
            }
        }

        private void SaveMission()
        {
            if (!File.Exists(_config.FlightMissionFile))
            {
                _notificationService.NotifyAboutFailure($"Отсутствует файл полетного задания {_config.FlightMissionFile}");
                return;
            }

            if (!File.Exists(_config.DroneScriptFile))
            {
                _notificationService.NotifyAboutFailure($"Отсутствует файл полетной программы {_config.DroneScriptFile}");
                return;
            }

            var client = GetSftpClient();

            try
            {
                client.Connect();

                UploadFileBySftp(client, _config.DroneScriptFile, _config.DroneFileStorage);
                UploadFileBySftp(client, _config.FlightMissionFile, _config.DroneFileStorage);

                _notificationService.NotifyAboutSuccess("Полетное задание сохранено на дрон");
            }
            catch (Exception ex)
            {
                _notificationService.NotifyAboutFailure("Ошибка сохранения файла. Проверьте подключение и логи.");
                _logger.Error(ex, ex.Message);
            }
            finally
            {
                client.Disconnect();
            }
        }

        private SftpClient GetSftpClient()
        {
            return new SftpClient(
                _config.DroneAddress,
                _config.DronePort,
                _config.DroneUsername,
                _config.DronePassword);
        }

        private void UploadFileBySftp(SftpClient client, string localFilePath, string remoteDirectory)
        {
            var fileName = Path.GetFileName(localFilePath);
            var remotePath = $"{remoteDirectory}{fileName}";

            using (var fileStream = new FileStream(localFilePath, FileMode.Open))
            {
                client.UploadFile(fileStream, remotePath);
            }
        }

        private void StartMission()
        {
            var client = GetSshClient();

            try
            {
                client.Connect();

                var scriptFile = Path.GetFileName(_config.DroneScriptFile);
                var remotePath = $"{_config.DroneFileStorage}{scriptFile}";

                using ShellStream shellStream = client.CreateShellStream("ShellName", 80, 24, 800, 600, 1024);

                string prompt = shellStream.Expect(new Regex(@"[$>]"));

                shellStream.WriteLine($"/usr/bin/python3 {remotePath}");
                //shellStream.WriteLine($"python3 {remotePath}");
    
                _notificationService.NotifyAboutSuccess("Полетное задание выполняется");
            }
            catch (Exception ex)
            {
                _notificationService.NotifyAboutFailure("Ошибка сохранения файла. Проверьте подключение и логи.");
                _logger.Error(ex, ex.Message);
            }
            finally
            {
                client.Disconnect();
            }
        }

        private SshClient GetSshClient()
        {
            return new SshClient(
                _config.DroneAddress,
                _config.DronePort,
                _config.DroneUsername,
                _config.DronePassword);
        }
    }
}