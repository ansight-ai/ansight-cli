#import "../AnsightSimulatorHid/_Xcode27SimulatorHid.m"

static void Require(BOOL condition, NSString *message)
{
    if (!condition)
    {
        fprintf(stderr, "FAIL: %s\n", message.UTF8String);
        exit(1);
    }
}

int main(void)
{
    @autoreleasepool
    {
        Require(!CoreSimulatorVersionRequiresXcode27Hid(nil), @"Unknown versions preserve the older transport.");
        Require(!CoreSimulatorVersionRequiresXcode27Hid(@"1051.49"), @"Xcode 26 retains Indigo input.");
        Require(!CoreSimulatorVersionRequiresXcode27Hid(@"1155.3"), @"The previous CoreSimulator keeps Indigo.");
        Require(CoreSimulatorVersionRequiresXcode27Hid(@"1155.4"), @"Use DTUHID at the known transition.");
        Require(CoreSimulatorVersionRequiresXcode27Hid(@"1155.10"), @"Compare build versions numerically.");

        dispatch_queue_t queue = dispatch_queue_create("ai.ansight.test-dtuhid-peer", DISPATCH_QUEUE_SERIAL);
        xpc_connection_t listener = xpc_connection_create(NULL, queue);
        NSMutableArray *peers = [NSMutableArray array];
        __block NSUInteger inputs = 0;
        __block NSUInteger barriers = 0;
        __block BOOL disconnectOnRequest = NO;
        xpc_connection_set_event_handler(listener, ^(xpc_object_t peer) {
            if (xpc_get_type(peer) != XPC_TYPE_CONNECTION)
            {
                return;
            }
            [peers addObject:peer];
            xpc_connection_set_event_handler(peer, ^(xpc_object_t message) {
                if (xpc_get_type(message) != XPC_TYPE_DICTIONARY)
                {
                    return;
                }
                if (disconnectOnRequest)
                {
                    xpc_connection_cancel(peer);
                    return;
                }

                const char *feature = xpc_dictionary_get_string(message, "featureIdentifier");
                Require(feature != NULL && strcmp(feature, "com.apple.coredevice.feature.remote.hid.digitizer") == 0,
                    @"Every message identifies the guest HID service.");
                if (!xpc_dictionary_get_bool(message, "isBarrier"))
                {
                    inputs++;
                    Require(strcmp(xpc_dictionary_get_string(message, "messageType"), "IndigoButtonEvent") == 0,
                        @"The input crosses the connection before its barrier.");
                    xpc_object_t payload = xpc_dictionary_get_value(message, "payload");
                    Require(xpc_dictionary_get_uint64(payload, "usageCode") == 0x40, @"Preserve Home's HID usage.");
                    return;
                }

                barriers++;
                Require(inputs == barriers, @"Each input is followed by exactly one acknowledgement barrier.");
                xpc_object_t payload = xpc_dictionary_get_value(message, "payload");
                Require(xpc_dictionary_get_uint64(payload, "usageCode") == 0, @"The barrier must not press a real key.");
                xpc_object_t reply = xpc_dictionary_create_reply(message);
                Require(reply != NULL, @"Wait for a peer reply rather than merely queueing the message.");
                xpc_connection_send_message(peer, reply);
            });
            xpc_connection_resume(peer);
        });
        xpc_connection_resume(listener);

        _Xcode27SimulatorHid *client = [_Xcode27SimulatorHid new];
        client.queue = dispatch_queue_create("ai.ansight.test-dtuhid-reply", DISPATCH_QUEUE_SERIAL);
        xpc_endpoint_t endpoint = xpc_endpoint_create(listener);
        client.connection = xpc_connection_create_from_endpoint(endpoint);
        xpc_connection_set_event_handler(client.connection, ^(xpc_object_t event) { (void)event; });
        xpc_connection_resume(client.connection);

        xpc_object_t payload = xpc_dictionary_create(NULL, NULL, 0);
        xpc_dictionary_set_uint64(payload, "usagePage", 0x0c);
        xpc_dictionary_set_uint64(payload, "usageCode", 0x40);
        xpc_dictionary_set_uint64(payload, "state", 1);
        NSError *error = nil;
        Require([client sendMessageType:@"IndigoButtonEvent" payload:payload error:&error],
            [NSString stringWithFormat:@"A live peer should acknowledge input: %@", error]);

        dispatch_sync(queue, ^{
            Require(inputs == 1 && barriers == 1, @"Exactly one input reaches the peer.");
            disconnectOnRequest = YES;
        });
        Require(![client sendMessageType:@"IndigoButtonEvent" payload:payload error:&error],
            @"A disconnected peer must not report delivery.");
        Require(error != nil, @"A disconnected peer provides an actionable error.");
        NSError *firstFailure = error;
        error = nil;
        Require(![client sendMessageType:@"IndigoButtonEvent" payload:payload error:&error],
            @"Do not silently replay an interrupted gesture.");
        Require(error == firstFailure, @"Preserve the original connection failure.");

        client = nil;
        xpc_connection_cancel(listener);
        dispatch_sync(queue, ^{
            for (xpc_connection_t peer in peers)
            {
                xpc_connection_cancel(peer);
            }
        });
        puts("PASS: DTUHID version selection, ordered XPC delivery, peer acknowledgement, interruption and no replay.");
    }
    return 0;
}
