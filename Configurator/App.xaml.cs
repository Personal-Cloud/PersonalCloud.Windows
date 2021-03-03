﻿using System;
using System.ComponentModel;
using System.Diagnostics;
using System.ServiceProcess;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

using Hardcodet.Wpf.TaskbarNotification;

using JKang.IpcServiceFramework.Client;
using JKang.IpcServiceFramework.Hosting;

using Microsoft.AppCenter;
using Microsoft.AppCenter.Analytics;
using Microsoft.AppCenter.Crashes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using NSPersonalCloud.WindowsConfigurator.IPC;
using NSPersonalCloud.WindowsContract;

namespace NSPersonalCloud.WindowsConfigurator
{
#pragma warning disable CA1001 // Objects are disposed at app shutdown.

    public partial class App : Application
#pragma warning restore CA1001
    {
        private const string CloudManagerClient = "Cloud Service";

        /*
        private const string DiskMounterClient = "Dokany Service";
        private const string StorageClient = "Database Service";
        */

        public TaskbarIcon TrayIcon { get; private set; }

        protected override void OnStartup(StartupEventArgs e)
        {
            AppCenter.Start("b3c9ef09-9572-4eab-bb3c-33e203d862ea", typeof(Analytics), typeof(Crashes));

            base.OnStartup(e);

            if (e.Args.Length != 0 && e.Args[0].EndsWith("startup", StringComparison.InvariantCultureIgnoreCase))
            {
                Globals.DoNotShowMainWinOnLaunch = true;
            }
        }

        private void OnApplicationStarted(object sender, StartupEventArgs e)
        {
            TrayIcon = (TaskbarIcon) FindResource("TrayIcon");

            /*
            var ipcServiceCollection = new ServiceCollection()
                .AddSingleton<PopupPresenter>()
                .AddSingleton<NotificationCenter>()
                .AddIpcClient(builder => {
                    builder.AddNamedPipe()
                           .AddService<IPopupPresenter>(services => services.GetRequiredService<PopupPresenter>())
                           .AddService<ICloudEventHandler>(services => services.GetRequiredService<NotificationCenter>());
                });

            _ = new IpcServiceHostBuilder(ipcServiceCollection.BuildServiceProvider())
                .AddNamedPipeEndpoint<IPopupPresenter>("Popups", Pipes.Messenger, true)
                .AddNamedPipeEndpoint<ICloudEventHandler>("Notifications", Pipes.NotificationCenter, true)
                .Build().RunAsync(runners.Token);
            */

            Globals.ServiceHost = Host.CreateDefaultBuilder().ConfigureServices(services => {
                services.AddSingleton<ICloudEventHandler, NotificationCenter>();
            }).ConfigureIpcHost(builder => {
                builder.AddNamedPipeEndpoint<ICloudEventHandler>(Pipes.NotificationCenter);
            }).ConfigureLogging(builder => {
                builder.SetMinimumLevel(Microsoft.Extensions.Logging.LogLevel.Information);
            }).Build();
            Globals.ServiceHost.StartAsync();

            Globals.ServiceContainer = new ServiceCollection().AddNamedPipeIpcClient<ICloudManager>(CloudManagerClient, Pipes.CloudAdmin)
                                                              .BuildServiceProvider();

            Globals.CloudManager = Globals.ServiceContainer.GetRequiredService<IIpcClientFactory<ICloudManager>>().CreateClient(CloudManagerClient);

            SetMainWindow();
            EnsureServiceHasBeenStarted(false);
            /*
            Task.Run(async () => {
                var cloud = await Globals.CloudManager.InvokeAsync(x => x.GetAllPersonalCloud()).ConfigureAwait(false);
                Globals.PersonalCloud = cloud.Length == 0 ? null : (Guid?) cloud[0];

                Dispatcher.Invoke(() => {
                    if (Globals.PersonalCloud != null) MainWindow = new MainWindow();
                    else MainWindow = new WelcomeWindow();
                    MainWindow.Show();
                });
            });
            */
        }

        void SetMainWindow()
        {
            Task.Run(async () => {
                var cloud = await Globals.CloudManager.InvokeAsync(x => x.GetAllPersonalCloud()).ConfigureAwait(false);
                Globals.PersonalCloud = cloud.Length == 0 ? null : (Guid?) cloud[0];

                Dispatcher.Invoke(() => {
                    if (Globals.PersonalCloud != null) MainWindow = new MainWindow();
                    else MainWindow = new WelcomeWindow();

                    if (!Globals.DoNotShowMainWinOnLaunch) MainWindow.Show();
                    Globals.DoNotShowMainWinOnLaunch = false;
                });
            });
        }

        private void OnApplicationExit(object sender, ExitEventArgs e)
        {
            Globals.ServiceContainer.Dispose();
            Globals.ServiceHost.Dispose();

            TrayIcon.Dispose();
        }
        public void EnsureServiceHasBeenStarted(bool showui=true)
        {
            Task.Run(async () => {
                try
                {
                    var cts = new CancellationTokenSource();
                    cts.CancelAfter(1000);
                    var ret = await Globals.CloudManager.InvokeAsync(x => x.Ping(666), cts.Token)
                                         .ConfigureAwait(false);
                    if (ret == 666)
                    {
                        return;
                    }
                }
                catch
                {
                }
                _ = Task.Run(() => {
                    RestartService();
                });
                if (showui)
                {
                    Application.Current.Dispatcher.Invoke(() => {
                        UIHelpers.ShowLoadingMessage();
                        MainWindow?.Hide();
                        MainWindow = null;
                    });
                }
            });
        }

        public void RestartService()
        {
            Dispatcher.Invoke(() => {
                MainWindow?.Close();
            });

            try
            {
                var service = new ServiceController(Services.ServiceName);
                if (service.Status != ServiceControllerStatus.Stopped
                    && service.Status != ServiceControllerStatus.StopPending
                    && service.CanStop)
                {
                    service.Stop();
                }

                service.Start();
            }
            catch (Exception exception)
            {
                if (exception is InvalidOperationException ioe
                    && ioe.InnerException is Win32Exception w32e
                    && w32e.NativeErrorCode == 5)
                {
                    using (var process = new Process())
                    {
                        process.StartInfo.FileName = "sc.exe";
                        process.StartInfo.Arguments = "stop PersonalCloud.WindowsService";
                        process.StartInfo.UseShellExecute = true;
                        process.StartInfo.Verb = "runas";
                        process.Start();
                        process.WaitForExit();
                    }

                    using (var process = new Process())
                    {
                        process.StartInfo.FileName = "sc.exe";
                        process.StartInfo.Arguments = "start PersonalCloud.WindowsService";
                        process.StartInfo.UseShellExecute = true;
                        process.StartInfo.Verb = "runas";
                        process.Start();
                        process.WaitForExit();
                    }
                }

                // Ignored.
            }
        }

        public void StopService()
        {
            Dispatcher.Invoke(() => {
                MainWindow?.Close();
            });

            try
            {
                var service = new ServiceController(Services.ServiceName);
                if (service.Status != ServiceControllerStatus.Stopped
                    && service.Status != ServiceControllerStatus.StopPending
                    && service.CanStop)
                {
                    service.Stop();
                }
            }
            catch (Exception exception)
            {
                if (exception is InvalidOperationException ioe
                    && ioe.InnerException is Win32Exception w32e
                    && w32e.NativeErrorCode == 5)
                {
                    using (var process = new Process())
                    {
                        process.StartInfo.FileName = "sc.exe";
                        process.StartInfo.Arguments = "stop PersonalCloud.WindowsService";
                        process.StartInfo.UseShellExecute = true;
                        process.StartInfo.Verb = "runas";
                        process.Start();
                        process.WaitForExit();
                    }
                }
            }

            Dispatcher.Invoke(() => {
                MainWindow?.Close();
                MainWindow = null;
            });
        }
    }
}
