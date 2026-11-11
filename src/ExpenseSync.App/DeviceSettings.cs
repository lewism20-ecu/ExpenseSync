namespace ExpenseSync.App;

public static class DeviceSettings
{
    public const string DeviceId =
#if ANDROID
        "device-02";
#else
        "device-01";
#endif

    public const string UserId =
#if ANDROID
        "user-02";
#else
        "user-01";
#endif
}
