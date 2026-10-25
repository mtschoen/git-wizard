namespace GitWizardTests.TestSupport;

readonly record struct FanotifyMarkCall(
    int Descriptor,
    uint Flags,
    ulong Mask,
    int DirectoryDescriptor,
    string Path);
