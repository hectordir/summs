namespace Summs;

static class Program
{
    [STAThread]
    static void Main()
    {
        using var mutex = new Mutex(true, "Summs_SingleInstance", out bool isNew);
        if (!isNew)
            return;

        ApplicationConfiguration.Initialize();
        Application.Run(new TrayContext());
    }
}
