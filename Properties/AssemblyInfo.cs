using System.Reflection;
using System.Runtime.InteropServices;
using Ftpush.Properties;

// General Information about an assembly is controlled through the following
// set of attributes. Change these attribute values to modify the information
// associated with an assembly.
[assembly: AssemblyTitle("ftpush")]
[assembly: AssemblyDescription("A command line tool for FTP directory push.")]
[assembly: AssemblyCompany("Affinity ID")]
[assembly: AssemblyCopyright("Copyright © Affinity ID 2016")]

// Setting ComVisible to false makes the types in this assembly not visible
// to COM components.  If you need to access a type in this assembly from
// COM, set the ComVisible attribute to true on that type.
[assembly: ComVisible(false)]

// The following GUID is for the ID of the typelib if this project is exposed to COM
[assembly: Guid("723d6be3-8499-4069-b613-7710baf404fc")]

// Version information for an assembly consists of the following four values:
//
//      Major Version
//      Minor Version
//      Build Number
//      Revision
//
// You can specify all the values or you can default the Build and Revision Numbers
// by using the '*' as shown below:
// [assembly: AssemblyVersion("1.0.*")]
[assembly: AssemblyVersion(AssemblyVersion.VersionString)]
[assembly: AssemblyFileVersion(AssemblyVersion.VersionString)]
[assembly: AssemblyInformationalVersion(AssemblyVersion.VersionString + AssemblyVersion.VersionSuffix)]

namespace Ftpush.Properties {
    internal static class AssemblyVersion {
        public const string VersionString = "0.8.0";
        public const string VersionSuffix = "-pre-05";
    }
}