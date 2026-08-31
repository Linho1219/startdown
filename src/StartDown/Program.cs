using StartDown.Infrastructure;
using StartDown.Runner;
using StartDown.UI;

namespace StartDown;

internal static class Program
{
    private const string RunnerMutexName = @"Local\StartDown.Runner";

    [STAThread]
    private static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);

        using var logger = new AppLogger();
        Application.ThreadException += (_, eventArgs) =>
        {
            logger.Error($"未处理的界面异常：{eventArgs.Exception}");
            MessageBox.Show(
                eventArgs.Exception.Message,
                "StartDown",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        };

        CommandLineOptions options;
        try
        {
            options = CommandLineOptions.Parse(args);
        }
        catch (ArgumentException exception)
        {
            logger.Error($"命令行无效：{exception.Message}");
            var startupRequested = args.Any(argument =>
                string.Equals(argument, "--startup", StringComparison.OrdinalIgnoreCase));
            if (!startupRequested)
            {
                MessageBox.Show(exception.Message, "StartDown 命令行无效", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            return 64;
        }
        var store = new ConfigurationStore(logger, options.ConfigurationPath);
        if (!options.Run)
        {
            try
            {
                Application.Run(new ConfigurationForm(store, logger));
                return 0;
            }
            catch (ConfigurationLoadException exception)
            {
                logger.Error(exception.Message);
                MessageBox.Show(exception.Message, "StartDown 配置读取失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 2;
            }
        }

        using var runnerMutex = new Mutex(initiallyOwned: false, RunnerMutexName);
        bool ownsRunnerMutex;
        try
        {
            ownsRunnerMutex = runnerMutex.WaitOne(0);
        }
        catch (AbandonedMutexException)
        {
            ownsRunnerMutex = true;
        }

        if (!ownsRunnerMutex)
        {
            logger.Warning("另一个 StartDown runner 已在运行，本次请求未执行。");
            if (options.ShowStatus)
            {
                MessageBox.Show(
                    "另一个 StartDown 启动任务正在运行。",
                    "StartDown",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
            }
            return 0;
        }

        void ReleaseRunnerMutex()
        {
            if (!ownsRunnerMutex)
            {
                return;
            }

            runnerMutex.ReleaseMutex();
            ownsRunnerMutex = false;
        }

        try
        {
            logger.Info($"Runner mode started (status window: {options.ShowStatus}, entry: {options.EntryId?.ToString() ?? "all"}).");
            var configuration = store.Load(requireExisting: true);
            var runner = new StartupRunner(configuration, options.EntryId, logger);
            EventHandler<RunnerCompletedEventArgs> releaseMutexOnCompletion = (_, _) => ReleaseRunnerMutex();
            runner.Completed += releaseMutexOnCompletion;

            RunnerApplicationContext? context = null;
            RunnerStatusForm? statusForm = null;
            if (options.ShowStatus)
            {
                logger.Info("Creating the runner status window.");
                statusForm = new RunnerStatusForm(runner, logger, () => context?.RequestExit());
                logger.Info("Runner status window created.");
            }

            context = new RunnerApplicationContext(
                runner,
                statusForm,
                keepStatusOpenAfterCompletion: options.ShowStatus);
            logger.Info("Entering the WinForms runner message loop.");
            try
            {
                Application.Run(context);
            }
            finally
            {
                runner.Completed -= releaseMutexOnCompletion;
            }
            return context.ExitCode;
        }
        catch (Exception exception)
        {
            logger.Error($"StartDown 启动失败：{exception}");
            if (!options.Startup || options.ShowStatus)
            {
                MessageBox.Show(exception.Message, "StartDown 启动失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            return 2;
        }
        finally
        {
            ReleaseRunnerMutex();
        }
    }
}
