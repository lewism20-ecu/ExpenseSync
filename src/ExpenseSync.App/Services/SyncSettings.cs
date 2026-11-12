namespace ExpenseSync.App.Services;

public static class SyncSettings
{
    // Set this to the shared HTTPS Dev Tunnel URL for both platforms; never include access tokens.
    private const string? SharedDevTunnelUrl = null;

    // Local defaults remain useful until the group has a tunnel. The trailing slash is required.
    public static string BaseUrl { get; set; } =
        SharedDevTunnelUrl ??
#if ANDROID
        "http://10.0.2.2:5157/";
#else
        "http://localhost:5157/";
#endif
}
