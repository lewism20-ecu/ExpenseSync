namespace ExpenseSync.App.Services;

public static class SyncSettings
{
    // Change this to your API address. Android emulators reach the host machine at 10.0.2.2;
    // physical devices need the host IP or a Dev Tunnel URL. The trailing slash is required.
    public static string BaseUrl { get; set; } =
#if ANDROID
        "http://10.0.2.2:5157/";
#else
        "http://localhost:5157/";
#endif
}
