// Named permission constants. Import from "./ansight-task.js" in task modules.

/** Frozen Permission names with exact native mappings documented on each member. */
export const Permission = Object.freeze({
  /**
   * Camera authorization.
   * Availability: Android emulators and authorized physical devices; iOS Simulator queries TCC. iOS mutations require camera in installed simctl privacy services.
   * iOS: camera. Android: android.permission.CAMERA.
   * All iOS mutations require the service in the installed simctl privacy list; physical iOS is unsupported.
   * @supportedPlatforms ios android
   * @supportedDeviceKinds virtual physical
   * @see https://developer.apple.com/documentation/avfoundation/requesting-authorization-to-capture-and-save-media
   * @see https://developer.android.com/reference/android/Manifest.permission#CAMERA
   */
  Camera: "camera",
  /**
   * Microphone authorization.
   * Availability: iOS Simulator; Android emulators and authorized physical devices.
   * iOS: microphone. Android: android.permission.RECORD_AUDIO.
   * All iOS mutations require the service in the installed simctl privacy list; physical iOS is unsupported.
   * @supportedPlatforms ios android
   * @supportedDeviceKinds virtual physical
   * @see https://developer.apple.com/documentation/avfoundation/requesting-authorization-to-capture-and-save-media
   * @see https://developer.android.com/reference/android/Manifest.permission#RECORD_AUDIO
   */
  Microphone: "microphone",
  /**
   * Contact access; the shared Android mapping grants read access only.
   * Availability: iOS Simulator; Android emulators and authorized physical devices.
   * iOS: contacts. Android: android.permission.READ_CONTACTS.
   * All iOS mutations require the service in the installed simctl privacy list; physical iOS is unsupported.
   * @supportedPlatforms ios android
   * @supportedDeviceKinds virtual physical
   * @see https://developer.apple.com/documentation/contacts/accessing-the-contact-store
   * @see https://developer.android.com/reference/android/Manifest.permission#READ_CONTACTS
   */
  Contacts: "contacts",
  /**
   * Calendar access; only manifest-declared Android read/write permissions are used.
   * Availability: iOS Simulator; Android emulators and authorized physical devices. EventKit full/write-only access is not separately modeled.
   * iOS: calendar. Android: android.permission.READ_CALENDAR / android.permission.WRITE_CALENDAR.
   * All iOS mutations require the service in the installed simctl privacy list; physical iOS is unsupported.
   * @supportedPlatforms ios android
   * @supportedDeviceKinds virtual physical
   * @see https://developer.apple.com/documentation/eventkit/accessing-the-event-store
   * @see https://developer.android.com/reference/android/Manifest.permission#READ_CALENDAR
   * @see https://developer.android.com/reference/android/Manifest.permission#WRITE_CALENDAR
   */
  Calendar: "calendar",
  /**
   * Photo-library access; limited native selections remain limited.
   * Availability: iOS Simulator; Android emulators and authorized physical devices. Android 14+ selected access is queried and revoked, but grant does not select particular media.
   * iOS: photos. Android: android.permission.READ_MEDIA_IMAGES / android.permission.READ_MEDIA_VIDEO on API 33+ with target SDK 33+; android.permission.READ_EXTERNAL_STORAGE otherwise.
   * All iOS mutations require the service in the installed simctl privacy list; physical iOS is unsupported.
   * @supportedPlatforms ios android
   * @supportedDeviceKinds virtual physical
   * @see https://developer.apple.com/documentation/photos/phaccesslevel
   * @see https://developer.android.com/reference/android/Manifest.permission#READ_MEDIA_IMAGES
   * @see https://developer.android.com/reference/android/Manifest.permission#READ_MEDIA_VIDEO
   * @see https://developer.android.com/reference/android/Manifest.permission#READ_EXTERNAL_STORAGE
   */
  Photos: "photos",
  /**
   * Foreground location; only manifest-declared Android coarse/fine permissions are used.
   * Availability: iOS Simulator; Android emulators and authorized physical devices.
   * iOS: location. Android: android.permission.ACCESS_COARSE_LOCATION / android.permission.ACCESS_FINE_LOCATION.
   * All iOS mutations require the service in the installed simctl privacy list; physical iOS is unsupported.
   * @supportedPlatforms ios android
   * @supportedDeviceKinds virtual physical
   * @see https://developer.apple.com/documentation/corelocation/cllocationmanager/requestwheninuseauthorization%28%29
   * @see https://developer.android.com/reference/android/Manifest.permission#ACCESS_COARSE_LOCATION
   * @see https://developer.android.com/reference/android/Manifest.permission#ACCESS_FINE_LOCATION
   */
  Location: "location",
  /**
   * Background location authorization.
   * Availability: iOS Simulator; Android emulators and authorized physical devices. Android grants declared foreground dependencies first. On API 29+, revoke removes background access only; iOS revoke denies location entirely.
   * iOS: location-always. Android: android.permission.ACCESS_BACKGROUND_LOCATION on API 29+; android.permission.ACCESS_COARSE_LOCATION / android.permission.ACCESS_FINE_LOCATION on older Android.
   * All iOS mutations require the service in the installed simctl privacy list; physical iOS is unsupported.
   * @supportedPlatforms ios android
   * @supportedDeviceKinds virtual physical
   * @see https://developer.apple.com/documentation/corelocation/cllocationmanager/requestalwaysauthorization%28%29
   * @see https://developer.android.com/reference/android/Manifest.permission#ACCESS_BACKGROUND_LOCATION
   * @see https://developer.android.com/reference/android/Manifest.permission#ACCESS_COARSE_LOCATION
   * @see https://developer.android.com/reference/android/Manifest.permission#ACCESS_FINE_LOCATION
   */
  LocationAlways: "locationAlways",
  /**
   * Media-library access; the Android equivalent covers shared audio only.
   * Availability: iOS Simulator; Android emulators and authorized physical devices.
   * iOS: media-library. Android: android.permission.READ_MEDIA_AUDIO on API 33+ with target SDK 33+; android.permission.READ_EXTERNAL_STORAGE otherwise.
   * All iOS mutations require the service in the installed simctl privacy list; physical iOS is unsupported.
   * @supportedPlatforms ios android
   * @supportedDeviceKinds virtual physical
   * @see https://developer.apple.com/documentation/mediaplayer/mpmedialibrary/requestauthorization%28_%3A%29
   * @see https://developer.android.com/reference/android/Manifest.permission#READ_MEDIA_AUDIO
   * @see https://developer.android.com/reference/android/Manifest.permission#READ_EXTERNAL_STORAGE
   */
  MediaLibrary: "mediaLibrary",
  /**
   * Motion/activity authorization; the Android mapping is activity recognition.
   * Availability: iOS Simulator; Android API 29+ on emulators and authorized physical devices. Unsupported on older Android.
   * iOS: motion. Android: android.permission.ACTIVITY_RECOGNITION.
   * All iOS mutations require the service in the installed simctl privacy list; physical iOS is unsupported.
   * @supportedPlatforms ios android
   * @supportedDeviceKinds virtual physical
   * @see https://developer.apple.com/documentation/coremotion/cmmotionactivitymanager
   * @see https://developer.android.com/reference/android/Manifest.permission#ACTIVITY_RECOGNITION
   */
  Motion: "motion",
  /**
   * Notification authorization.
   * Availability: Android API 33+ on emulators and authorized physical devices. Unsupported on iOS and older Android.
   * iOS: unsupported (no simctl provider for notification authorization). Android: android.permission.POST_NOTIFICATIONS.
   * There is no iOS notification mutation or query backend in this provider.
   * @supportedPlatforms android
   * @unsupportedPlatforms ios
   * @supportedDeviceKinds virtual physical
   * @see https://developer.apple.com/documentation/usernotifications/asking-permission-to-use-notifications
   * @see https://developer.android.com/reference/android/Manifest.permission#POST_NOTIFICATIONS
   */
  Notifications: "notifications"
});

/** Frozen IosPermission names with exact native mappings documented on each member. */
export const IosPermission = Object.freeze({
  /**
   * Native iOS camera authorization.
   * Availability: iOS Simulator only; physical iOS and Android are unsupported. Mutations require this service in the installed simctl privacy list.
   * iOS: simctl privacy service "camera". Android: no native mapping; use shared Permission names for resource equivalents.
   * Queries TCC. Mutations require camera in installed simctl services; the provider does not supply camera hardware.
   * @supportedPlatforms ios
   * @unsupportedPlatforms android
   * @supportedDeviceKinds virtual
   * @unsupportedDeviceKinds physical
   * @see https://developer.apple.com/documentation/avfoundation/requesting-authorization-to-capture-and-save-media
   */
  Camera: "camera",
  /**
   * Native iOS microphone authorization.
   * Availability: iOS Simulator only; physical iOS and Android are unsupported. Mutations require this service in the installed simctl privacy list.
   * iOS: simctl privacy service "microphone". Android: no native mapping; use shared Permission names for resource equivalents.
   * Queries read the Simulator authorization store; unreadable state is unknown.
   * @supportedPlatforms ios
   * @unsupportedPlatforms android
   * @supportedDeviceKinds virtual
   * @unsupportedDeviceKinds physical
   * @see https://developer.apple.com/documentation/avfoundation/requesting-authorization-to-capture-and-save-media
   */
  Microphone: "microphone",
  /**
   * Native iOS contacts authorization.
   * Availability: iOS Simulator only; physical iOS and Android are unsupported. Mutations require this service in the installed simctl privacy list.
   * iOS: simctl privacy service "contacts". Android: no native mapping; use shared Permission names for resource equivalents.
   * Queries read the Simulator authorization store; unreadable state is unknown.
   * @supportedPlatforms ios
   * @unsupportedPlatforms android
   * @supportedDeviceKinds virtual
   * @unsupportedDeviceKinds physical
   * @see https://developer.apple.com/documentation/contacts/accessing-the-contact-store
   */
  Contacts: "contacts",
  /**
   * Native iOS contacts-limited authorization.
   * Availability: iOS Simulator only; physical iOS and Android are unsupported. Mutations require this service in the installed simctl privacy list.
   * iOS: simctl privacy service "contacts-limited". Android: no native mapping; use shared Permission names for resource equivalents.
   * Requires a Simulator runtime and simctl service with limited-contact support. Does not choose specific contacts.
   * @supportedPlatforms ios
   * @unsupportedPlatforms android
   * @supportedDeviceKinds virtual
   * @unsupportedDeviceKinds physical
   * @see https://developer.apple.com/documentation/contacts/cnauthorizationstatus/limited
   */
  ContactsLimited: "contacts-limited",
  /**
   * Native iOS calendar authorization.
   * Availability: iOS Simulator only; physical iOS and Android are unsupported. Mutations require this service in the installed simctl privacy list.
   * iOS: simctl privacy service "calendar". Android: no native mapping; use shared Permission names for resource equivalents.
   * Controls the simctl calendar service; EventKit full/write-only access is not separately modeled.
   * @supportedPlatforms ios
   * @unsupportedPlatforms android
   * @supportedDeviceKinds virtual
   * @unsupportedDeviceKinds physical
   * @see https://developer.apple.com/documentation/eventkit/accessing-the-event-store
   */
  Calendar: "calendar",
  /**
   * Native iOS photos authorization.
   * Availability: iOS Simulator only; physical iOS and Android are unsupported. Mutations require this service in the installed simctl privacy list.
   * iOS: simctl privacy service "photos". Android: no native mapping; use shared Permission names for resource equivalents.
   * Controls photo-library authorization; limited user selections can remain limited.
   * @supportedPlatforms ios
   * @unsupportedPlatforms android
   * @supportedDeviceKinds virtual
   * @unsupportedDeviceKinds physical
   * @see https://developer.apple.com/documentation/photos/phaccesslevel
   */
  Photos: "photos",
  /**
   * Native iOS photos-add authorization.
   * Availability: iOS Simulator only; physical iOS and Android are unsupported. Mutations require this service in the installed simctl privacy list.
   * iOS: simctl privacy service "photos-add". Android: no native mapping; use shared Permission names for resource equivalents.
   * Controls add-only photo authorization; grants no photo-library read access.
   * @supportedPlatforms ios
   * @unsupportedPlatforms android
   * @supportedDeviceKinds virtual
   * @unsupportedDeviceKinds physical
   * @see https://developer.apple.com/documentation/photos/phaccesslevel
   */
  PhotosAdd: "photos-add",
  /**
   * Native iOS location authorization.
   * Availability: iOS Simulator only; physical iOS and Android are unsupported. Mutations require this service in the installed simctl privacy list.
   * iOS: simctl privacy service "location". Android: no native mapping; use shared Permission names for resource equivalents.
   * When-in-use location. Authorization is distinct from simulated device coordinates.
   * @supportedPlatforms ios
   * @unsupportedPlatforms android
   * @supportedDeviceKinds virtual
   * @unsupportedDeviceKinds physical
   * @see https://developer.apple.com/documentation/corelocation/cllocationmanager/requestwheninuseauthorization%28%29
   */
  Location: "location",
  /**
   * Native iOS location-always authorization.
   * Availability: iOS Simulator only; physical iOS and Android are unsupported. Mutations require this service in the installed simctl privacy list.
   * iOS: simctl privacy service "location-always". Android: no native mapping; use shared Permission names for resource equivalents.
   * Always location. Revoking this simctl service denies all location access.
   * @supportedPlatforms ios
   * @unsupportedPlatforms android
   * @supportedDeviceKinds virtual
   * @unsupportedDeviceKinds physical
   * @see https://developer.apple.com/documentation/corelocation/cllocationmanager/requestalwaysauthorization%28%29
   */
  LocationAlways: "location-always",
  /**
   * Native iOS media-library authorization.
   * Availability: iOS Simulator only; physical iOS and Android are unsupported. Mutations require this service in the installed simctl privacy list.
   * iOS: simctl privacy service "media-library". Android: no native mapping; use shared Permission names for resource equivalents.
   * Controls media-library authorization; does not populate the Simulator with music.
   * @supportedPlatforms ios
   * @unsupportedPlatforms android
   * @supportedDeviceKinds virtual
   * @unsupportedDeviceKinds physical
   * @see https://developer.apple.com/documentation/mediaplayer/mpmedialibrary/requestauthorization%28_%3A%29
   */
  MediaLibrary: "media-library",
  /**
   * Native iOS motion authorization.
   * Availability: iOS Simulator only; physical iOS and Android are unsupported. Mutations require this service in the installed simctl privacy list.
   * iOS: simctl privacy service "motion". Android: no native mapping; use shared Permission names for resource equivalents.
   * Authorization is distinct from hardware or motion-data availability.
   * @supportedPlatforms ios
   * @unsupportedPlatforms android
   * @supportedDeviceKinds virtual
   * @unsupportedDeviceKinds physical
   * @see https://developer.apple.com/documentation/coremotion/cmmotionactivitymanager
   */
  Motion: "motion",
  /**
   * Native iOS reminders authorization.
   * Availability: iOS Simulator only; physical iOS and Android are unsupported. Mutations require this service in the installed simctl privacy list.
   * iOS: simctl privacy service "reminders". Android: no native mapping; use shared Permission names for resource equivalents.
   * Queries read the Simulator authorization store; unreadable state is unknown.
   * @supportedPlatforms ios
   * @unsupportedPlatforms android
   * @supportedDeviceKinds virtual
   * @unsupportedDeviceKinds physical
   * @see https://developer.apple.com/documentation/eventkit/accessing-the-event-store
   */
  Reminders: "reminders",
  /**
   * Native iOS siri authorization.
   * Availability: iOS Simulator only; physical iOS and Android are unsupported. Mutations require this service in the installed simctl privacy list.
   * iOS: simctl privacy service "siri". Android: no native mapping; use shared Permission names for resource equivalents.
   * Controls SiriKit authorization; Siri capability and app configuration are still required.
   * @supportedPlatforms ios
   * @unsupportedPlatforms android
   * @supportedDeviceKinds virtual
   * @unsupportedDeviceKinds physical
   * @see https://developer.apple.com/documentation/sirikit/requesting-authorization-to-use-siri
   */
  Siri: "siri"
});

/** Frozen AndroidPermission names with exact native mappings documented on each member. */
export const AndroidPermission = Object.freeze({
  /**
   * Camera access.
   * Availability: Android API 1+ on emulators and authorized physical devices; iOS is unsupported. Runtime grant/revoke require API 23+, a declared runtime-managed permission, and native policy permitting pm control.
   * Android: android.permission.CAMERA. iOS: no native mapping.
   * Device services and app roles can further constrain effective access.
   * @supportedPlatforms android
   * @unsupportedPlatforms ios
   * @supportedDeviceKinds virtual physical
   * @see https://developer.android.com/reference/android/Manifest.permission#CAMERA
   */
  Camera: "android.permission.CAMERA",
  /**
   * Microphone recording.
   * Availability: Android API 1+ on emulators and authorized physical devices; iOS is unsupported. Runtime grant/revoke require API 23+, a declared runtime-managed permission, and native policy permitting pm control.
   * Android: android.permission.RECORD_AUDIO. iOS: no native mapping.
   * Device services and app roles can further constrain effective access.
   * @supportedPlatforms android
   * @unsupportedPlatforms ios
   * @supportedDeviceKinds virtual physical
   * @see https://developer.android.com/reference/android/Manifest.permission#RECORD_AUDIO
   */
  RecordAudio: "android.permission.RECORD_AUDIO",
  /**
   * Read contact records.
   * Availability: Android API 1+ on emulators and authorized physical devices; iOS is unsupported. Runtime grant/revoke require API 23+, a declared runtime-managed permission, and native policy permitting pm control.
   * Android: android.permission.READ_CONTACTS. iOS: no native mapping.
   * Device services and app roles can further constrain effective access.
   * @supportedPlatforms android
   * @unsupportedPlatforms ios
   * @supportedDeviceKinds virtual physical
   * @see https://developer.android.com/reference/android/Manifest.permission#READ_CONTACTS
   */
  ReadContacts: "android.permission.READ_CONTACTS",
  /**
   * Modify contact records.
   * Availability: Android API 1+ on emulators and authorized physical devices; iOS is unsupported. Runtime grant/revoke require API 23+, a declared runtime-managed permission, and native policy permitting pm control.
   * Android: android.permission.WRITE_CONTACTS. iOS: no native mapping.
   * Device services and app roles can further constrain effective access.
   * @supportedPlatforms android
   * @unsupportedPlatforms ios
   * @supportedDeviceKinds virtual physical
   * @see https://developer.android.com/reference/android/Manifest.permission#WRITE_CONTACTS
   */
  WriteContacts: "android.permission.WRITE_CONTACTS",
  /**
   * Access account listings.
   * Availability: Android API 1+ on emulators and authorized physical devices; iOS is unsupported. Runtime grant/revoke require API 23+, a declared runtime-managed permission, and native policy permitting pm control.
   * Android: android.permission.GET_ACCOUNTS. iOS: no native mapping.
   * Does not sign in to accounts or grant account-provider access; signature and account-visibility rules still apply.
   * @supportedPlatforms android
   * @unsupportedPlatforms ios
   * @supportedDeviceKinds virtual physical
   * @see https://developer.android.com/reference/android/Manifest.permission#GET_ACCOUNTS
   */
  GetAccounts: "android.permission.GET_ACCOUNTS",
  /**
   * Read calendar entries.
   * Availability: Android API 1+ on emulators and authorized physical devices; iOS is unsupported. Runtime grant/revoke require API 23+, a declared runtime-managed permission, and native policy permitting pm control.
   * Android: android.permission.READ_CALENDAR. iOS: no native mapping.
   * Device services and app roles can further constrain effective access.
   * @supportedPlatforms android
   * @unsupportedPlatforms ios
   * @supportedDeviceKinds virtual physical
   * @see https://developer.android.com/reference/android/Manifest.permission#READ_CALENDAR
   */
  ReadCalendar: "android.permission.READ_CALENDAR",
  /**
   * Modify calendar entries.
   * Availability: Android API 1+ on emulators and authorized physical devices; iOS is unsupported. Runtime grant/revoke require API 23+, a declared runtime-managed permission, and native policy permitting pm control.
   * Android: android.permission.WRITE_CALENDAR. iOS: no native mapping.
   * Device services and app roles can further constrain effective access.
   * @supportedPlatforms android
   * @unsupportedPlatforms ios
   * @supportedDeviceKinds virtual physical
   * @see https://developer.android.com/reference/android/Manifest.permission#WRITE_CALENDAR
   */
  WriteCalendar: "android.permission.WRITE_CALENDAR",
  /**
   * Approximate foreground location.
   * Availability: Android API 1+ on emulators and authorized physical devices; iOS is unsupported. Runtime grant/revoke require API 23+, a declared runtime-managed permission, and native policy permitting pm control.
   * Android: android.permission.ACCESS_COARSE_LOCATION. iOS: no native mapping.
   * Device services and app roles can further constrain effective access.
   * @supportedPlatforms android
   * @unsupportedPlatforms ios
   * @supportedDeviceKinds virtual physical
   * @see https://developer.android.com/reference/android/Manifest.permission#ACCESS_COARSE_LOCATION
   */
  AccessCoarseLocation: "android.permission.ACCESS_COARSE_LOCATION",
  /**
   * Precise foreground location.
   * Availability: Android API 1+ on emulators and authorized physical devices; iOS is unsupported. Runtime grant/revoke require API 23+, a declared runtime-managed permission, and native policy permitting pm control.
   * Android: android.permission.ACCESS_FINE_LOCATION. iOS: no native mapping.
   * Approximate user choices can limit effective precision; authorization is distinct from device location settings.
   * @supportedPlatforms android
   * @unsupportedPlatforms ios
   * @supportedDeviceKinds virtual physical
   * @see https://developer.android.com/reference/android/Manifest.permission#ACCESS_FINE_LOCATION
   */
  AccessFineLocation: "android.permission.ACCESS_FINE_LOCATION",
  /**
   * Background location.
   * Availability: Android API 29+ on emulators and authorized physical devices; iOS is unsupported. Runtime grant/revoke require API 23+, a declared runtime-managed permission, and native policy permitting pm control.
   * Android: android.permission.ACCESS_BACKGROUND_LOCATION. iOS: no native mapping.
   * Installer allowlisting is required; pm does not bypass native restrictions. Requires declared and granted foreground coarse/fine location. Native calls do not automatically grant dependencies.
   * @supportedPlatforms android
   * @unsupportedPlatforms ios
   * @supportedDeviceKinds virtual physical
   * @see https://developer.android.com/reference/android/Manifest.permission#ACCESS_BACKGROUND_LOCATION
   */
  AccessBackgroundLocation: "android.permission.ACCESS_BACKGROUND_LOCATION",
  /**
   * Read images in shared media storage.
   * Availability: Android API 33+ on emulators and authorized physical devices; iOS is unsupported. Runtime grant/revoke require API 23+, a declared runtime-managed permission, and native policy permitting pm control.
   * Android: android.permission.READ_MEDIA_IMAGES. iOS: no native mapping.
   * Use for apps targeting API 33+; older targets use READ_EXTERNAL_STORAGE.
   * @supportedPlatforms android
   * @unsupportedPlatforms ios
   * @supportedDeviceKinds virtual physical
   * @see https://developer.android.com/reference/android/Manifest.permission#READ_MEDIA_IMAGES
   */
  ReadMediaImages: "android.permission.READ_MEDIA_IMAGES",
  /**
   * Read videos in shared media storage.
   * Availability: Android API 33+ on emulators and authorized physical devices; iOS is unsupported. Runtime grant/revoke require API 23+, a declared runtime-managed permission, and native policy permitting pm control.
   * Android: android.permission.READ_MEDIA_VIDEO. iOS: no native mapping.
   * Use for apps targeting API 33+; older targets use READ_EXTERNAL_STORAGE.
   * @supportedPlatforms android
   * @unsupportedPlatforms ios
   * @supportedDeviceKinds virtual physical
   * @see https://developer.android.com/reference/android/Manifest.permission#READ_MEDIA_VIDEO
   */
  ReadMediaVideo: "android.permission.READ_MEDIA_VIDEO",
  /**
   * Read audio in shared media storage.
   * Availability: Android API 33+ on emulators and authorized physical devices; iOS is unsupported. Runtime grant/revoke require API 23+, a declared runtime-managed permission, and native policy permitting pm control.
   * Android: android.permission.READ_MEDIA_AUDIO. iOS: no native mapping.
   * Use for apps targeting API 33+; older targets use READ_EXTERNAL_STORAGE.
   * @supportedPlatforms android
   * @unsupportedPlatforms ios
   * @supportedDeviceKinds virtual physical
   * @see https://developer.android.com/reference/android/Manifest.permission#READ_MEDIA_AUDIO
   */
  ReadMediaAudio: "android.permission.READ_MEDIA_AUDIO",
  /**
   * Access visual media selected by the user.
   * Availability: Android API 34+ on emulators and authorized physical devices; iOS is unsupported. Runtime grant/revoke require API 23+, a declared runtime-managed permission, and native policy permitting pm control.
   * Android: android.permission.READ_MEDIA_VISUAL_USER_SELECTED. iOS: no native mapping.
   * Does not choose particular media. Use with declared READ_MEDIA_IMAGES/READ_MEDIA_VIDEO as appropriate; shared Photos revoke also clears this permission.
   * @supportedPlatforms android
   * @unsupportedPlatforms ios
   * @supportedDeviceKinds virtual physical
   * @see https://developer.android.com/reference/android/Manifest.permission#READ_MEDIA_VISUAL_USER_SELECTED
   */
  ReadMediaVisualUserSelected: "android.permission.READ_MEDIA_VISUAL_USER_SELECTED",
  /**
   * Legacy external-storage read access.
   * Availability: Android API 16+ on emulators and authorized physical devices; iOS is unsupported. Runtime grant/revoke require API 23+, a declared runtime-managed permission, and native policy permitting pm control.
   * Android: android.permission.READ_EXTERNAL_STORAGE. iOS: no native mapping.
   * Legacy storage access is constrained by OS, target SDK, scoped storage, and installer policy. Use granular media permissions for target SDK 33+.
   * @supportedPlatforms android
   * @unsupportedPlatforms ios
   * @supportedDeviceKinds virtual physical
   * @see https://developer.android.com/reference/android/Manifest.permission#READ_EXTERNAL_STORAGE
   */
  ReadExternalStorage: "android.permission.READ_EXTERNAL_STORAGE",
  /**
   * Legacy external-storage write access.
   * Availability: Android API 4+ on emulators and authorized physical devices; iOS is unsupported. Runtime grant/revoke require API 23+, a declared runtime-managed permission, and native policy permitting pm control.
   * Android: android.permission.WRITE_EXTERNAL_STORAGE. iOS: no native mapping.
   * Has no storage-access effect for apps targeting API 30+.
   * @supportedPlatforms android
   * @unsupportedPlatforms ios
   * @supportedDeviceKinds virtual physical
   * @see https://developer.android.com/reference/android/Manifest.permission#WRITE_EXTERNAL_STORAGE
   */
  WriteExternalStorage: "android.permission.WRITE_EXTERNAL_STORAGE",
  /**
   * Recognize physical activity.
   * Availability: Android API 29+ on emulators and authorized physical devices; iOS is unsupported. Runtime grant/revoke require API 23+, a declared runtime-managed permission, and native policy permitting pm control.
   * Android: android.permission.ACTIVITY_RECOGNITION. iOS: no native mapping.
   * Device services and app roles can further constrain effective access.
   * @supportedPlatforms android
   * @unsupportedPlatforms ios
   * @supportedDeviceKinds virtual physical
   * @see https://developer.android.com/reference/android/Manifest.permission#ACTIVITY_RECOGNITION
   */
  ActivityRecognition: "android.permission.ACTIVITY_RECOGNITION",
  /**
   * Body-sensor measurements.
   * Availability: Android API 20+ on emulators and authorized physical devices; iOS is unsupported. Runtime grant/revoke require API 23+, a declared runtime-managed permission, and native policy permitting pm control.
   * Android: android.permission.BODY_SENSORS. iOS: no native mapping.
   * Apps targeting API 36+ use granular android.permission.health identifiers for affected sensor APIs.
   * @supportedPlatforms android
   * @unsupportedPlatforms ios
   * @supportedDeviceKinds virtual physical
   * @see https://developer.android.com/reference/android/Manifest.permission#BODY_SENSORS
   * @see https://developer.android.com/about/versions/16/behavior-changes-16
   */
  BodySensors: "android.permission.BODY_SENSORS",
  /**
   * Background body-sensor measurements.
   * Availability: Android API 33+ on emulators and authorized physical devices; iOS is unsupported. Runtime grant/revoke require API 23+, a declared runtime-managed permission, and native policy permitting pm control.
   * Android: android.permission.BODY_SENSORS_BACKGROUND. iOS: no native mapping.
   * Installer allowlisting is required; pm does not bypass native restrictions. Requires BODY_SENSORS first. Apps targeting API 36+ use READ_HEALTH_DATA_IN_BACKGROUND for affected APIs.
   * @supportedPlatforms android
   * @unsupportedPlatforms ios
   * @supportedDeviceKinds virtual physical
   * @see https://developer.android.com/reference/android/Manifest.permission#BODY_SENSORS_BACKGROUND
   * @see https://developer.android.com/about/versions/16/behavior-changes-16
   */
  BodySensorsBackground: "android.permission.BODY_SENSORS_BACKGROUND",
  /**
   * Post app notifications.
   * Availability: Android API 33+ on emulators and authorized physical devices; iOS is unsupported. Runtime grant/revoke require API 23+, a declared runtime-managed permission, and native policy permitting pm control.
   * Android: android.permission.POST_NOTIFICATIONS. iOS: no native mapping.
   * Device services and app roles can further constrain effective access.
   * @supportedPlatforms android
   * @unsupportedPlatforms ios
   * @supportedDeviceKinds virtual physical
   * @see https://developer.android.com/reference/android/Manifest.permission#POST_NOTIFICATIONS
   */
  PostNotifications: "android.permission.POST_NOTIFICATIONS",
  /**
   * Discover nearby Bluetooth devices.
   * Availability: Android API 31+ on emulators and authorized physical devices; iOS is unsupported. Runtime grant/revoke require API 23+, a declared runtime-managed permission, and native policy permitting pm control.
   * Android: android.permission.BLUETOOTH_SCAN. iOS: no native mapping.
   * Device services and app roles can further constrain effective access.
   * @supportedPlatforms android
   * @unsupportedPlatforms ios
   * @supportedDeviceKinds virtual physical
   * @see https://developer.android.com/reference/android/Manifest.permission#BLUETOOTH_SCAN
   */
  BluetoothScan: "android.permission.BLUETOOTH_SCAN",
  /**
   * Communicate with Bluetooth devices.
   * Availability: Android API 31+ on emulators and authorized physical devices; iOS is unsupported. Runtime grant/revoke require API 23+, a declared runtime-managed permission, and native policy permitting pm control.
   * Android: android.permission.BLUETOOTH_CONNECT. iOS: no native mapping.
   * Device services and app roles can further constrain effective access.
   * @supportedPlatforms android
   * @unsupportedPlatforms ios
   * @supportedDeviceKinds virtual physical
   * @see https://developer.android.com/reference/android/Manifest.permission#BLUETOOTH_CONNECT
   */
  BluetoothConnect: "android.permission.BLUETOOTH_CONNECT",
  /**
   * Advertise over Bluetooth.
   * Availability: Android API 31+ on emulators and authorized physical devices; iOS is unsupported. Runtime grant/revoke require API 23+, a declared runtime-managed permission, and native policy permitting pm control.
   * Android: android.permission.BLUETOOTH_ADVERTISE. iOS: no native mapping.
   * Device services and app roles can further constrain effective access.
   * @supportedPlatforms android
   * @unsupportedPlatforms ios
   * @supportedDeviceKinds virtual physical
   * @see https://developer.android.com/reference/android/Manifest.permission#BLUETOOTH_ADVERTISE
   */
  BluetoothAdvertise: "android.permission.BLUETOOTH_ADVERTISE",
  /**
   * Nearby Wi-Fi device operations.
   * Availability: Android API 33+ on emulators and authorized physical devices; iOS is unsupported. Runtime grant/revoke require API 23+, a declared runtime-managed permission, and native policy permitting pm control.
   * Android: android.permission.NEARBY_WIFI_DEVICES. iOS: no native mapping.
   * Device services and app roles can further constrain effective access.
   * @supportedPlatforms android
   * @unsupportedPlatforms ios
   * @supportedDeviceKinds virtual physical
   * @see https://developer.android.com/reference/android/Manifest.permission#NEARBY_WIFI_DEVICES
   */
  NearbyWifiDevices: "android.permission.NEARBY_WIFI_DEVICES",
  /**
   * Read telephony state.
   * Availability: Android API 1+ on emulators and authorized physical devices; iOS is unsupported. Runtime grant/revoke require API 23+, a declared runtime-managed permission, and native policy permitting pm control.
   * Android: android.permission.READ_PHONE_STATE. iOS: no native mapping.
   * Device services and app roles can further constrain effective access.
   * @supportedPlatforms android
   * @unsupportedPlatforms ios
   * @supportedDeviceKinds virtual physical
   * @see https://developer.android.com/reference/android/Manifest.permission#READ_PHONE_STATE
   */
  ReadPhoneState: "android.permission.READ_PHONE_STATE",
  /**
   * Read device phone numbers.
   * Availability: Android API 26+ on emulators and authorized physical devices; iOS is unsupported. Runtime grant/revoke require API 23+, a declared runtime-managed permission, and native policy permitting pm control.
   * Android: android.permission.READ_PHONE_NUMBERS. iOS: no native mapping.
   * Device services and app roles can further constrain effective access.
   * @supportedPlatforms android
   * @unsupportedPlatforms ios
   * @supportedDeviceKinds virtual physical
   * @see https://developer.android.com/reference/android/Manifest.permission#READ_PHONE_NUMBERS
   */
  ReadPhoneNumbers: "android.permission.READ_PHONE_NUMBERS",
  /**
   * Place phone calls.
   * Availability: Android API 1+ on emulators and authorized physical devices; iOS is unsupported. Runtime grant/revoke require API 23+, a declared runtime-managed permission, and native policy permitting pm control.
   * Android: android.permission.CALL_PHONE. iOS: no native mapping.
   * Device services and app roles can further constrain effective access.
   * @supportedPlatforms android
   * @unsupportedPlatforms ios
   * @supportedDeviceKinds virtual physical
   * @see https://developer.android.com/reference/android/Manifest.permission#CALL_PHONE
   */
  CallPhone: "android.permission.CALL_PHONE",
  /**
   * Answer incoming phone calls.
   * Availability: Android API 26+ on emulators and authorized physical devices; iOS is unsupported. Runtime grant/revoke require API 23+, a declared runtime-managed permission, and native policy permitting pm control.
   * Android: android.permission.ANSWER_PHONE_CALLS. iOS: no native mapping.
   * Device services and app roles can further constrain effective access.
   * @supportedPlatforms android
   * @unsupportedPlatforms ios
   * @supportedDeviceKinds virtual physical
   * @see https://developer.android.com/reference/android/Manifest.permission#ANSWER_PHONE_CALLS
   */
  AnswerPhoneCalls: "android.permission.ANSWER_PHONE_CALLS",
  /**
   * Read call history.
   * Availability: Android API 16+ on emulators and authorized physical devices; iOS is unsupported. Runtime grant/revoke require API 23+, a declared runtime-managed permission, and native policy permitting pm control.
   * Android: android.permission.READ_CALL_LOG. iOS: no native mapping.
   * Installer allowlisting is required; pm does not bypass native restrictions.
   * @supportedPlatforms android
   * @unsupportedPlatforms ios
   * @supportedDeviceKinds virtual physical
   * @see https://developer.android.com/reference/android/Manifest.permission#READ_CALL_LOG
   */
  ReadCallLog: "android.permission.READ_CALL_LOG",
  /**
   * Modify call history.
   * Availability: Android API 16+ on emulators and authorized physical devices; iOS is unsupported. Runtime grant/revoke require API 23+, a declared runtime-managed permission, and native policy permitting pm control.
   * Android: android.permission.WRITE_CALL_LOG. iOS: no native mapping.
   * Installer allowlisting is required; pm does not bypass native restrictions.
   * @supportedPlatforms android
   * @unsupportedPlatforms ios
   * @supportedDeviceKinds virtual physical
   * @see https://developer.android.com/reference/android/Manifest.permission#WRITE_CALL_LOG
   */
  WriteCallLog: "android.permission.WRITE_CALL_LOG",
  /**
   * Send SMS messages.
   * Availability: Android API 1+ on emulators and authorized physical devices; iOS is unsupported. Runtime grant/revoke require API 23+, a declared runtime-managed permission, and native policy permitting pm control.
   * Android: android.permission.SEND_SMS. iOS: no native mapping.
   * Installer allowlisting is required; pm does not bypass native restrictions.
   * @supportedPlatforms android
   * @unsupportedPlatforms ios
   * @supportedDeviceKinds virtual physical
   * @see https://developer.android.com/reference/android/Manifest.permission#SEND_SMS
   */
  SendSms: "android.permission.SEND_SMS",
  /**
   * Read SMS messages.
   * Availability: Android API 1+ on emulators and authorized physical devices; iOS is unsupported. Runtime grant/revoke require API 23+, a declared runtime-managed permission, and native policy permitting pm control.
   * Android: android.permission.READ_SMS. iOS: no native mapping.
   * Installer allowlisting is required; pm does not bypass native restrictions.
   * @supportedPlatforms android
   * @unsupportedPlatforms ios
   * @supportedDeviceKinds virtual physical
   * @see https://developer.android.com/reference/android/Manifest.permission#READ_SMS
   */
  ReadSms: "android.permission.READ_SMS",
  /**
   * Receive SMS messages.
   * Availability: Android API 1+ on emulators and authorized physical devices; iOS is unsupported. Runtime grant/revoke require API 23+, a declared runtime-managed permission, and native policy permitting pm control.
   * Android: android.permission.RECEIVE_SMS. iOS: no native mapping.
   * Installer allowlisting is required; pm does not bypass native restrictions.
   * @supportedPlatforms android
   * @unsupportedPlatforms ios
   * @supportedDeviceKinds virtual physical
   * @see https://developer.android.com/reference/android/Manifest.permission#RECEIVE_SMS
   */
  ReceiveSms: "android.permission.RECEIVE_SMS",
  /**
   * Receive MMS messages.
   * Availability: Android API 1+ on emulators and authorized physical devices; iOS is unsupported. Runtime grant/revoke require API 23+, a declared runtime-managed permission, and native policy permitting pm control.
   * Android: android.permission.RECEIVE_MMS. iOS: no native mapping.
   * Installer allowlisting is required; pm does not bypass native restrictions.
   * @supportedPlatforms android
   * @unsupportedPlatforms ios
   * @supportedDeviceKinds virtual physical
   * @see https://developer.android.com/reference/android/Manifest.permission#RECEIVE_MMS
   */
  ReceiveMms: "android.permission.RECEIVE_MMS",
  /**
   * Receive WAP push messages.
   * Availability: Android API 1+ on emulators and authorized physical devices; iOS is unsupported. Runtime grant/revoke require API 23+, a declared runtime-managed permission, and native policy permitting pm control.
   * Android: android.permission.RECEIVE_WAP_PUSH. iOS: no native mapping.
   * Installer allowlisting is required; pm does not bypass native restrictions.
   * @supportedPlatforms android
   * @unsupportedPlatforms ios
   * @supportedDeviceKinds virtual physical
   * @see https://developer.android.com/reference/android/Manifest.permission#RECEIVE_WAP_PUSH
   */
  ReceiveWapPush: "android.permission.RECEIVE_WAP_PUSH"
});
