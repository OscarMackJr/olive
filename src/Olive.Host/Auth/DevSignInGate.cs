using Olive.Application;

namespace Olive.Host.Auth;

// D-02 / D-18: presence marks the /dev/sign-in picker as non-production only, so FND-05 refuses a production start while it is registered.
public sealed class DevSignInGate : INonProductionOnly;
