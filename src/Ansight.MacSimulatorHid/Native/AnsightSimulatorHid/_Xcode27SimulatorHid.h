#import <Foundation/Foundation.h>
#import <xpc/xpc.h>

// CoreSimulator is installed system-wide; its version, rather than the selected
// Xcode version, determines whether the guest suppresses legacy Indigo input.
BOOL AnsightSimulatorRequiresXcode27Hid(void);
BOOL _Xcode27SimulatorHidIsAvailable(void);

@interface _Xcode27SimulatorHid : NSObject

- (instancetype)initWithDevice:(id)device
            developerDirectory:(NSString *)developerDirectory
                    deviceUdid:(NSString *)deviceUdid
                         error:(NSError **)error;
- (BOOL)sendMessageType:(NSString *)messageType payload:(xpc_object_t)payload error:(NSError **)error;
- (BOOL)sendStylusSample:(NSDictionary *)sample error:(NSError **)error;

@end
