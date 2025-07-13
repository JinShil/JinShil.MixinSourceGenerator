using System.Reflection;
using System.Runtime.CompilerServices;
using JinShil.MixinSourceGenerator;

namespace JinShil.MixinSourceGenerator.Test
{
    public interface ITestInterface
    {
        void InterfacePublicMethod1();
    }

    [AttributeUsage(AttributeTargets.All, AllowMultiple = false)]
    public class TestAttribute : Attribute
    { }

    public struct ImplementationStruct
    {
        /// <summary>
        /// Test static field in struct.
        /// </summary>
        [Test]
        public static int StructPublicStaticField1;

        /// <summary>
        /// Test readonly static property in struct.
        /// </summary>
        [Test]
        public static void StructPublicStaticMethod1()
        { }

        /// <summary>
        /// Test static property in struct.
        /// </summary>
        [Test]
        public static int StructPublicStaticProperty1 { get; set; }

        /// <summary>
        /// Test field in struct.
        /// </summary>
        [Test]
        public int StructPublicField1;

        /// <summary>
        /// Test readonly property in struct.
        /// </summary>
        [Test]
        public int StructPublicReadonlyProperty1 { get; }

        /// <summary>
        /// Test property in struct.
        /// </summary>
        [Test]
        public int StructPublicProperty1 { get; set; }

        /// <summary>
        /// Test method in struct.
        /// </summary>
        [Test]
        public void StructPublicMethod1()
        { }

        /// <summary>
        /// Test event in struct.
        /// </summary>
        [Test]
        public event Action? StructPublicEvent1;

        /// <summary>
        /// Test method to fire the event in struct.
        /// </summary>
        [Test]
        private void FireStructPublicEvent1()
        {
            StructPublicEvent1?.Invoke();
        }
    }

    public class ImplementationClass : ITestInterface
    {
        // This should not be generated as a part of the implementation
        public ImplementationClass()
        { }

        /// <summary>
        /// Test static field in struct.
        /// </summary>
        [Test]
        public static int ClassPublicStaticField1;

        /// <summary>
        /// Test readonly static property in struct.
        /// </summary>
        [Test]
        public static void ClassPublicStaticMethod1()
        { }

        /// <summary>
        /// Test static property in struct.
        /// </summary>
        [Test]
        public static int ClassPublicStaticProperty1 { get; set; }

        /// <summary>
        /// Test field in class.
        /// </summary>
        [Test]
        public int ClassPublicField1;

        /// <summary>
        /// Test readonly property in class.
        /// </summary>
        [Test]
        public int ClassPublicReadonlyProperty1 { get; }

        /// <summary>
        /// Test property in class.
        /// </summary>
        [Test]
        public int ClassPublicProperty1 { get; set; }

        /// <summary>
        /// Test method in class.
        /// </summary>
        [Test]
        public void ClassPublicMethod1()
        { }

        /// <summary>
        /// Test event in class.
        /// </summary>
        [Test]
        public event Action? ClassPublicEvent1;

        /// <summary>
        /// Test method to fire the event in class.
        /// </summary>
        [Test]
        private void FireClassPublicEvent1()
        {
            ClassPublicEvent1?.Invoke();
        }

        /// <summary>
        /// Test explicit implementation of interface method.
        /// </summary>
        [Test]
        void ITestInterface.InterfacePublicMethod1()
        { }
    }

    public class ImplementationClassWithProtected
    {
        /// <summary>
        /// Test protected field in class.
        /// </summary>
        [Test]
        protected int ClassProtectedField1;

        /// <summary>
        /// Test protected readonly property in class.
        /// </summary>
        [Test]
        protected int ClassProtectedReadonlyProperty1 { get; }

        /// <summary>
        /// Test protected property in class.
        /// </summary>
        [Test]
        protected int ClassProtectedProperty1 { get; set; }

        /// <summary>
        /// Test protected method in class.
        /// </summary>
        [Test]
        protected void ClassProtectedMethod1()
        { }
    }

    [Mixin(typeof(ImplementationClass))]
    [Mixin(typeof(ImplementationStruct))]
    partial class CompositeClass : ITestInterface
    {
        
    }

    [Mixin(typeof(ImplementationStruct))]
    [Mixin(typeof(ImplementationClass))]
    partial struct CompositeStruct : ITestInterface
    {
        
    }

    [Mixin(typeof(ImplementationClass))]
    [Mixin(typeof(ImplementationStruct))]
    [Mixin(typeof(ImplementationClassWithProtected))]
    partial class CompositeClassWithProtected : ITestInterface
    {

    }

    class DerivedCompositeClassWithProtected : CompositeClassWithProtected
    {
        public DerivedCompositeClassWithProtected()
        {
            // Test generation of protected members
            ClassProtectedField1 = 42;
            var value = ClassProtectedReadonlyProperty1;
            ClassProtectedProperty1 = 42;
            ClassProtectedMethod1();
        }
    }

    public class Basics
    {
        [Fact]
        public void Test()
        {
            bool checkForTestAttribute(MemberInfo member)
            {
                return member.GetCustomAttributes(typeof(TestAttribute), false).Length > 0;
            }

            bool checkForTestAttributes(Type type)
            {
                MemberInfo[] members = type.GetMembers(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                foreach (var member in members)
                {
                    // Skip fields that are auto-generated by the compiler (like backing fields for properties)
                    if (member.MemberType == MemberTypes.Field && ((FieldInfo)member).IsDefined(typeof(CompilerGeneratedAttribute)))
                    {
                        continue;
                    }

                    // Skip special names (like property accessors, events, etc.)
                    if (member.MemberType == MemberTypes.Method && ((MethodInfo)member).IsSpecialName)
                    {
                        continue;
                    }

                    // Skip constructors
                    if (member.MemberType == MemberTypes.Constructor)
                    {
                        continue;
                    }

                    if (!checkForTestAttribute(member))
                    {
                        return false;
                    }
                }
                return true;
            }

            var compositeClass = new CompositeClass();
            CompositeClass.StructPublicStaticField1 += 42;
            CompositeClass.StructPublicStaticMethod1();
            CompositeClass.StructPublicStaticProperty1 += 42;
            CompositeClass.ClassPublicStaticField1 += 42;
            CompositeClass.ClassPublicStaticMethod1();
            CompositeClass.ClassPublicStaticProperty1 += 42;
            compositeClass.ClassPublicField1 += 42;
            var compositeClassValue = compositeClass.ClassPublicReadonlyProperty1;
            compositeClass.ClassPublicProperty1 += 42;
            compositeClass.ClassPublicMethod1();
            compositeClass.ClassPublicEvent1 += () => { };
            compositeClass.StructPublicField1 += 42;
            compositeClass.StructPublicProperty1 += 42;
            compositeClass.StructPublicMethod1();
            ((ITestInterface)compositeClass).InterfacePublicMethod1();
            Assert.True(checkForTestAttributes(typeof(CompositeClass)));

            var compositeStruct = new CompositeStruct();
            CompositeStruct.StructPublicStaticField1 += 42;
            CompositeStruct.StructPublicStaticMethod1();
            CompositeStruct.StructPublicStaticProperty1 += 42;
            CompositeStruct.ClassPublicStaticField1 += 42;
            CompositeStruct.ClassPublicStaticMethod1();
            CompositeStruct.ClassPublicStaticProperty1 += 42;
            compositeStruct.ClassPublicField1 += 42;
            var compositeStructValue = compositeStruct.ClassPublicReadonlyProperty1;
            compositeStruct.ClassPublicProperty1 += 42;
            compositeStruct.ClassPublicMethod1();
            compositeStruct.ClassPublicEvent1 += () => { };
            compositeStruct.StructPublicField1 += 42;
            compositeStruct.StructPublicProperty1 += 42;
            compositeStruct.StructPublicMethod1();
            ((ITestInterface)compositeStruct).InterfacePublicMethod1();
            Assert.True(checkForTestAttributes(typeof(CompositeStruct)));

            var compositeClassWithProtected = new CompositeClassWithProtected();
            CompositeClassWithProtected.StructPublicStaticField1 += 42;
            CompositeClassWithProtected.StructPublicStaticMethod1();
            CompositeClassWithProtected.StructPublicStaticProperty1 += 42;
            CompositeClassWithProtected.ClassPublicStaticField1 += 42;
            CompositeClassWithProtected.ClassPublicStaticMethod1();
            CompositeClassWithProtected.ClassPublicStaticProperty1 += 42;
            compositeClassWithProtected.ClassPublicField1 += 42;
            var compositeClassWithProtectedValue = compositeClass.ClassPublicReadonlyProperty1;
            compositeClassWithProtected.ClassPublicProperty1 += 42;
            compositeClassWithProtected.ClassPublicMethod1();
            compositeClassWithProtected.ClassPublicEvent1 += () => { };
            compositeClassWithProtected.StructPublicField1 += 42;
            compositeClassWithProtected.StructPublicProperty1 += 42;
            compositeClassWithProtected.StructPublicMethod1();
            ((ITestInterface)compositeClassWithProtected).InterfacePublicMethod1();
            Assert.True(checkForTestAttributes(typeof(CompositeClassWithProtected)));
        }
    }

    class Implementation1
    {
        public int Property1 { get; set; }

        public void Method1()
        { }
    }

    [Mixin(typeof(Implementation1))]
    partial class Implementation2
    {
        public int Property2 { get; set; }
        public void Method2()
        { }
    }

    [Mixin(typeof(Implementation2))]
    partial class Implementation1PlusImplementation2
    {

    }

    public class Recursion
    {
        [Fact]
        public void Test()
        {
            var i = new Implementation1PlusImplementation2();
            i.Property1 += 42;
            i.Method1();
            i.Property2 += 42;
            i.Method2();
        }
    }

    class IgnoreMixin
    {
        [MixinIgnore]
        public int IgnoredProperty { get; set; }

        [MixinIgnore]
        public int IgnoredField;

        [MixinIgnore]
        public event Action? IgnoredEvent;

        [MixinIgnore]
        public void IgnoredMethod()
        { }

        public void Method1()
        { 
            IgnoredProperty += 42;
            IgnoredField += 42;
            IgnoredEvent?.Invoke();
            IgnoredMethod();
        }
    }

    [Mixin(typeof(IgnoreMixin))]
    public partial class IgnoreComposition
    {
        public int IgnoredProperty { get; set; }

        public int IgnoredField;

        public event Action? IgnoredEvent;

        public bool Method1Called { get; private set; }
        public void IgnoredMethod()
        { 
            Method1Called = true;
        }
    }

    public class Ignore
    {
        [Fact]
        public void Test()
        {
            bool eventFired = false;
            var i = new IgnoreComposition();
            i.IgnoredEvent += () => { eventFired = true; };
            i.Method1();
            Assert.Equal(42, i.IgnoredProperty);
            Assert.Equal(42, i.IgnoredField);
            Assert.True(eventFired, "Ignored event was not fired.");
            Assert.True(i.Method1Called, "Ignored method was not called.");
        }
    }
}