using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using OpenRiaServices.DomainServices.Server;
using OpenRiaServices.DomainServices.Server.Test.Utilities;
using OpenRiaServices.DomainServices.Tools.SharedTypes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ServerClassLib;

namespace OpenRiaServices.DomainServices.Tools.Test
{
    /// <summary>
    /// Tests for SharedAssembliesManaged service
    /// </summary>
    [TestClass]
    public class SharedAssembliesManagedTests
    {
        public SharedAssembliesManagedTests()
        {
        }
        internal virtual ISharedAssemblies CreatedSharedAssembliesService(IEnumerable<string> assemblies, IEnumerable<string> assemblySearchPats, ILogger logger)
        {
            return new SharedAssembliesManaged(assemblies, assemblySearchPats, logger);
        }

        [DeploymentItem(@"OpenRiaServices.DomainServices.Tools\Test\ProjectPath.txt", "SAT")]
        [Description("SharedAssembliesManaged service locates shared types between projects")]
        [TestMethod]
        public void SharedAssembliesManaged_Types()
        {
            string projectPath = null;
            string outputPath = null;
            TestHelper.GetProjectPaths("SAT", out projectPath, out outputPath);
            string clientProjectPath = CodeGenHelper.ClientClassLibProjectPath(projectPath);
            List<string> assemblies = CodeGenHelper.ClientClassLibReferences(clientProjectPath, true);

            ConsoleLogger logger = new ConsoleLogger();
            var sa = CreatedSharedAssembliesService(assemblies, CodeGenHelper.GetSilverlightPaths(), logger);

            string sharedTypeLocation = GetSharedTypeLocation(sa, typeof(TestEntity));
            Assert.IsNotNull(sharedTypeLocation, "Expected TestEntity type to be shared");
            Assert.IsTrue(sharedTypeLocation.Contains("ClientClassLib"), "Expected to find type in client class lib");

            sharedTypeLocation = GetSharedTypeLocation(sa, typeof(TestValidator));
            Assert.IsNotNull(sharedTypeLocation, "Expected TestValidator type to be shared");
            Assert.IsTrue(sharedTypeLocation.Contains("ClientClassLib"), "Expected to find type in client class lib");

            sharedTypeLocation = GetSharedTypeLocation(sa, typeof(DomainService));
            Assert.IsNull(sharedTypeLocation, "Expected DomainService type not to be shared");

            sharedTypeLocation = GetSharedTypeLocation(sa, typeof(TestValidatorServer));
            Assert.IsNull(sharedTypeLocation, "Expected TestValidatorServer type not to be shared");

            TestHelper.AssertNoErrorsOrWarnings(logger);
        }

        [DeploymentItem(@"OpenRiaServices.DomainServices.Tools\Test\ProjectPath.txt", "SAT")]
        [Description("SharedAssembliesManaged service locates shared methods between projects")]
        [TestMethod]
        public void SharedAssembliesManaged_Methods()
        {
            string projectPath = null;
            string outputPath = null;
            TestHelper.GetProjectPaths("SAT", out projectPath, out outputPath);
            string clientProjectPath = CodeGenHelper.ClientClassLibProjectPath(projectPath);
            List<string> assemblies = CodeGenHelper.ClientClassLibReferences(clientProjectPath, true);

            ConsoleLogger logger = new ConsoleLogger();
            var sa = CreatedSharedAssembliesService(assemblies, CodeGenHelper.GetSilverlightPaths(), logger);

            var sharedMethodLocation = GetSharedMethodLocation(sa, typeof(TestValidator), "IsValid", new[] { typeof(TestEntity), typeof(ValidationContext) });
            Assert.IsNotNull(sharedMethodLocation, "Expected TestValidator.IsValid to be shared");
            Assert.IsTrue(sharedMethodLocation.Contains("ClientClassLib"), "Expected to find method in client class lib");

            sharedMethodLocation = GetSharedMethodLocation(sa, typeof(TestEntity), "ServerAndClientMethod", Type.EmptyTypes);
            Assert.IsNotNull(sharedMethodLocation, "Expected TestEntity.ServerAndClientMethod to be shared");
            Assert.IsTrue(sharedMethodLocation.Contains("ClientClassLib"), "Expected to find method in client class lib");

            sharedMethodLocation = GetSharedMethodLocation(sa, typeof(TestValidator), "ServertMethod", Type.EmptyTypes);
            Assert.IsNull(sharedMethodLocation, "Expected TestValidator.ServerMethod not to be shared");

            TestHelper.AssertNoErrorsOrWarnings(logger);
        }

        [DeploymentItem(@"OpenRiaServices.DomainServices.Tools\Test\ProjectPath.txt", "SAT")]
        [Description("SharedAssembliesManaged matches mscorlib types and methods")]
        [WorkItem(723391)]  // XElement entry below is regression for this
        [TestMethod]
        public void SharedAssembliesManaged_Matches_MsCorLib()
        {
            Type[] sharedTypes = new Type[] {
                typeof(Int32),
                typeof(string),
                typeof(Decimal),
            };

            Type[] nonSharedTypes = new Type[] {
                typeof(SerializableAttribute),
                //typeof(System.Xml.Linq.XElement)
            };

            MethodBase[] sharedMethods = new MethodBase[] {
                typeof(string).GetMethod("CopyTo"),
            };

            string projectPath = null;
            string outputPath = null;

            TestHelper.GetProjectPaths("SAT", out projectPath, out outputPath);
            string clientProjectPath = CodeGenHelper.ClientClassLibProjectPath(projectPath);
            List<string> assemblies = CodeGenHelper.ClientClassLibReferences(clientProjectPath, true);

            ConsoleLogger logger = new ConsoleLogger();
            var sa = CreatedSharedAssembliesService(assemblies, CodeGenHelper.GetSilverlightPaths(), logger);
            foreach (Type t in sharedTypes)
            {
                var sharedTypeLocation = GetSharedTypeLocation(sa, t);
                Assert.IsNotNull(sharedTypeLocation, "Expected type " + t.Name + " to be considered shared.");
            }

            foreach (MethodBase m in sharedMethods)
            {
                Type[] parameterTypes = m.GetParameters().Select(p => p.ParameterType).ToArray();
                var sharedMethodLocation = GetSharedMethodLocation(sa, m.DeclaringType, m.Name, parameterTypes);
                Assert.IsNotNull(sharedMethodLocation, "Expected method " + m.DeclaringType.Name + "." + m.Name + " to be considered shared.");
            }

            foreach (Type t in nonSharedTypes)
            {
                var sType = GetSharedTypeLocation(sa, t);
                Assert.IsNull(sType, "Expected type " + t.Name + " to be considered *not* shared.");
            }
        }



        [DeploymentItem(@"OpenRiaServices.DomainServices.Tools\Test\ProjectPath.txt", "SAT")]
        [Description("SharedAssembliesManaged service logs an info message for nonexistent assembly file")]
        [TestMethod]
        public void SharedAssembliesManaged_Logs_Message_NonExistent_Assembly()
        {
            ConsoleLogger logger = new ConsoleLogger();
            string file = "DoesNotExist.dll";
            var sa = CreatedSharedAssembliesService(new string[] { file }, CodeGenHelper.GetSilverlightPaths(), logger);

            var sharedType = GetSharedTypeLocation(sa, typeof(TestEntity));
            Assert.IsNull(sharedType, "Should not have detected any shared type.");

            string message = string.Format(CultureInfo.CurrentCulture, Resource.ClientCodeGen_Assembly_Load_Error, file, null);
            TestHelper.AssertHasInfoThatStartsWith(logger, message);
        }

        [DeploymentItem(@"OpenRiaServices.DomainServices.Tools\Test\ProjectPath.txt", "SAT")]
        [Description("SharedAssembliesManaged service logs an info message for bad image format assembly file")]
        [TestMethod]
        public void SharedAssembliesManaged_Logs_Message_BadImageFormat_Assembly()
        {
            // Create fake DLL with bad image 
            string assemblyFileName = Path.Combine(Path.GetTempPath(), (Guid.NewGuid().ToString() + ".dll"));
            File.WriteAllText(assemblyFileName, "neener neener neener");

            ConsoleLogger logger = new ConsoleLogger();

            var sa = CreatedSharedAssembliesService(new string[] { assemblyFileName }, CodeGenHelper.GetSilverlightPaths(), logger);

            var sharedType = GetSharedTypeLocation(sa, typeof(TestEntity));
            Assert.IsNull(sharedType, "Should not have detected any shared type.");

            string errorMessage = null;
            try
            {
                Assembly.ReflectionOnlyLoadFrom(assemblyFileName);
            }
            catch (BadImageFormatException bife)
            {
                errorMessage = bife.Message;
            }
            finally
            {
                File.Delete(assemblyFileName);
            }
            string message = string.Format(CultureInfo.CurrentCulture, Resource.ClientCodeGen_Assembly_Load_Error, assemblyFileName, null);
            TestHelper.AssertHasInfoThatStartsWith(logger, message);
        }

        private static string GetSharedTypeLocation(ISharedAssemblies sa, Type type)
        {
            var key = CodeMemberKey.CreateTypeKey(type.AssemblyQualifiedName);
            return sa.GetSharedAssemblyPath(key);
        }

        private static string GetSharedMethodLocation(ISharedAssemblies sa, Type type, string methodName, Type[] parameterTypes)
        {
            var key = CodeMemberKey.CreateMethodKey(type.AssemblyQualifiedName, methodName, parameterTypes.Select(t => t.AssemblyQualifiedName).ToArray());
            return sa.GetSharedAssemblyPath(key);
        }

        private static string GetSharedPropertyLocation(ISharedAssemblies sa, Type type, string propertyName)
        {
            var key = CodeMemberKey.CreatePropertyKey(type.AssemblyQualifiedName, propertyName);
            return sa.GetSharedAssemblyPath(key);
        }
    }
}
