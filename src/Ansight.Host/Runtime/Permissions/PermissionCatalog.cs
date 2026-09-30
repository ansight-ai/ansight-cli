namespace Ansight.Host.Runtime.Permissions;

internal static class PermissionCatalog
{
    // Shared names describe the closest native resource; the result exposes the exact mapping.
    public static readonly IReadOnlyDictionary<string, string> Ios = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["camera"] = "camera", ["microphone"] = "microphone", ["contacts"] = "contacts",
        ["calendar"] = "calendar", ["photos"] = "photos", ["location"] = "location",
        ["locationAlways"] = "location-always", ["mediaLibrary"] = "media-library",
        ["motion"] = "motion", ["notifications"] = "notifications"
    };

    public static readonly IReadOnlyDictionary<string, string> TccServices = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["camera"] = "kTCCServiceCamera", ["microphone"] = "kTCCServiceMicrophone",
        ["contacts"] = "kTCCServiceAddressBook", ["contacts-limited"] = "kTCCServiceContactsLimited",
        ["calendar"] = "kTCCServiceCalendar", ["photos"] = "kTCCServicePhotos",
        ["photos-add"] = "kTCCServicePhotosAdd", ["media-library"] = "kTCCServiceMediaLibrary",
        ["motion"] = "kTCCServiceMotion", ["reminders"] = "kTCCServiceReminders", ["siri"] = "kTCCServiceSiri"
    };

    public static string[] Android(string permission, int apiLevel, int targetSdk) => permission switch
    {
        "camera" => ["android.permission.CAMERA"],
        "microphone" => ["android.permission.RECORD_AUDIO"],
        "contacts" => ["android.permission.READ_CONTACTS"],
        "calendar" => ["android.permission.READ_CALENDAR", "android.permission.WRITE_CALENDAR"],
        "location" => ["android.permission.ACCESS_COARSE_LOCATION", "android.permission.ACCESS_FINE_LOCATION"],
        "locationAlways" when apiLevel >= 29 => ["android.permission.ACCESS_BACKGROUND_LOCATION"],
        "locationAlways" => ["android.permission.ACCESS_COARSE_LOCATION", "android.permission.ACCESS_FINE_LOCATION"],
        "photos" when apiLevel >= 33 && targetSdk >= 33 => ["android.permission.READ_MEDIA_IMAGES", "android.permission.READ_MEDIA_VIDEO"],
        "photos" => ["android.permission.READ_EXTERNAL_STORAGE"],
        "mediaLibrary" when apiLevel >= 33 && targetSdk >= 33 => ["android.permission.READ_MEDIA_AUDIO"],
        "mediaLibrary" => ["android.permission.READ_EXTERNAL_STORAGE"],
        "motion" when apiLevel >= 29 => ["android.permission.ACTIVITY_RECOGNITION"],
        "notifications" when apiLevel >= 33 => ["android.permission.POST_NOTIFICATIONS"],
        _ => []
    };
}

internal sealed record NativePermissionState(string Permission, string Status);

internal sealed record PermissionResult(
    string Operation, string Permission, string Platform, string DeviceId, string BundleIdentifier,
    bool Supported, bool IsSuccess, string Status, string Backend, string? Message,
    IReadOnlyList<NativePermissionState> NativePermissions);
