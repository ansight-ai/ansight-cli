#include "AnsightSimulatorHid.h"
#include <stdio.h>
#include <string.h>
#include <unistd.h>

int main(int argc, const char *argv[])
{
    if (argc != 4)
    {
        fprintf(stderr, "Usage: input <developer-directory> <simulator-udid> <check|finger|pencil|focus-keyboard|key|home>\n");
        return 2;
    }

    char error[4096] = {0};
    if (!AnsightSimulatorHidCheckCompatibility(argv[1], error, sizeof(error)))
    {
        fprintf(stderr, "%s\n", error);
        return 1;
    }

    if (strcmp(argv[3], "check") == 0)
    {
        puts("Framework compatibility passed.");
        return 0;
    }

    AnsightSimulatorHidSessionRef session = AnsightSimulatorHidSessionCreate(argv[1], error, sizeof(error));
    if (session == NULL)
    {
        fprintf(stderr, "%s\n", error);
        return 1;
    }

    bool success = true;
    if (strcmp(argv[3], "key") == 0)
    {
        success = AnsightSimulatorHidSessionSendKey(session, argv[2], 4,
            AnsightSimulatorKeyPhaseDown, error, sizeof(error));
        usleep(100000);
        success = success && AnsightSimulatorHidSessionSendKey(session, argv[2], 4,
            AnsightSimulatorKeyPhaseUp, error, sizeof(error));
        usleep(100000);
    }
    else if (strcmp(argv[3], "home") == 0)
    {
        success = AnsightSimulatorHidSessionSendButton(session, argv[2], AnsightSimulatorButtonHome,
            AnsightSimulatorButtonPhaseDown, error, sizeof(error));
        usleep(100000);
        success = success && AnsightSimulatorHidSessionSendButton(session, argv[2], AnsightSimulatorButtonHome,
            AnsightSimulatorButtonPhaseUp, error, sizeof(error));
    }
    else
    {
        for (int phase = 0; phase < 3 && success; phase++)
        {
            if (strcmp(argv[3], "pencil") == 0)
            {
                double seconds;
                uint64_t timestamp = AnsightSimulatorHidCreateTimestamp(&seconds);
                success = AnsightSimulatorHidSessionSendStylus(session, argv[2], phase,
                    0.4 + phase * 0.05, 0.5, phase == 2 ? 0 : 0.7, 0.7, 1.0, 0, 0, 17,
                    timestamp, error, sizeof(error));
            }
            else if (strcmp(argv[3], "finger") == 0 || strcmp(argv[3], "focus-keyboard") == 0)
            {
                bool focusKeyboard = strcmp(argv[3], "focus-keyboard") == 0;
                double x = focusKeyboard ? 0.5 : 0.4 + phase * 0.05;
                double y = focusKeyboard ? 0.1 : 0.5;
                success = AnsightSimulatorHidSessionSendPointer(session, argv[2], phase,
                    x, y, false, 0, 0, 17, 0, error, sizeof(error));
            }
            else
            {
                snprintf(error, sizeof(error), "Unknown input action: %s", argv[3]);
                success = false;
            }

            usleep(100000);
        }
    }

    AnsightSimulatorHidSessionDestroy(session);
    if (!success)
    {
        fprintf(stderr, "%s\n", error);
    }
    else
    {
        puts("Input accepted by transport; verify app evidence separately.");
    }

    return success ? 0 : 1;
}
