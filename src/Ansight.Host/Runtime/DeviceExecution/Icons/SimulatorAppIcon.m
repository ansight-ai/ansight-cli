#import <UIKit/UIKit.h>
#import <objc/message.h>

// Runs outside the observed app, in the selected iOS simulator. This optional
// UIKit selector supplies the same icon (including the default) as SpringBoard.
int main(int argc, char **argv) {
    @autoreleasepool {
        if (argc != 2) return 2;
        SEL selector = NSSelectorFromString(@"_applicationIconImageForBundleIdentifier:format:scale:");
        if (![UIImage respondsToSelector:selector]) return 3;
        UIImage *icon = ((UIImage *(*)(id, SEL, id, int, CGFloat))objc_msgSend)(
            [UIImage class], selector, [NSString stringWithUTF8String:argv[1]], 2, 2.0);
        NSData *png = icon ? UIImagePNGRepresentation(icon) : nil;
        if (!png) return 4;
        printf("ansight-icon:%s\n", [[png base64EncodedStringWithOptions:0] UTF8String]);
        return 0;
    }
}
