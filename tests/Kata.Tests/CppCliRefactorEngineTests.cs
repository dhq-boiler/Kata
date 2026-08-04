using Kata.Core.Diff;
using Kata.Core.Intents;
using Kata.Core.Model;
using Kata.Cpp;

namespace Kata.Tests;

public sealed class CppCliRefactorEngineTests : IDisposable
{
    private readonly string _sandbox;
    private readonly string _projectDir;
    private readonly string _vcxprojPath;

    public CppCliRefactorEngineTests()
    {
        _sandbox = Path.Combine(Path.GetTempPath(), "kata-cpp-refactor-" + Guid.NewGuid().ToString("N"));
        _projectDir = Path.Combine(_sandbox, "myNative");
        Directory.CreateDirectory(_projectDir);

        _vcxprojPath = Path.Combine(_projectDir, "myNative.vcxproj");
        File.WriteAllText(_vcxprojPath,
            """
            <?xml version="1.0" encoding="utf-8"?>
            <Project DefaultTargets="Build" xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
              <ItemGroup>
                <ClInclude Include="Existing.h" />
              </ItemGroup>
              <ItemGroup>
                <ClCompile Include="Existing.cpp" />
              </ItemGroup>
            </Project>
            """);
    }

    public void Dispose()
    {
        try { Directory.Delete(_sandbox, recursive: true); } catch { }
    }

    private CppTargetProject Target() => new(_vcxprojPath, _projectDir, "myNative");

    [Fact]
    public void AddGhostType_creates_header_and_updates_vcxproj()
    {
        var intent = new AddGhostTypeIntent
        {
            Source = IntentSource.Human,
            ProposedName = "Widget",
            Namespace = new NamespaceRef("myNative"),
            Kind = TypeKind.Class,
        };

        var changes = CppCliRefactorEngine.AddGhostType(Target(), intent);

        var added = changes.Single(c => c.Kind == DocumentChangeKind.Added);
        var modified = changes.Single(c => c.Kind == DocumentChangeKind.Modified);

        Assert.EndsWith("Widget.h", added.FilePath);
        Assert.Contains("#pragma once", added.NewText!);
        Assert.Contains("namespace myNative", added.NewText!);
        Assert.Contains("public ref class Widget { };", added.NewText!);

        Assert.EndsWith("myNative.vcxproj", modified.FilePath);
        Assert.Contains("<ClInclude Include=\"Widget.h\"", modified.NewText!);
        // Original ClInclude preserved
        Assert.Contains("Existing.h", modified.NewText!);
    }

    [Fact]
    public void AddGhostType_maps_interface_and_enum_and_struct_kinds()
    {
        var iface = CppCliRefactorEngine.AddGhostType(Target(), new AddGhostTypeIntent
        {
            Source = IntentSource.Human, ProposedName = "IFoo",
            Namespace = new NamespaceRef("myNative"), Kind = TypeKind.Interface,
        });
        var enum_ = CppCliRefactorEngine.AddGhostType(Target(), new AddGhostTypeIntent
        {
            Source = IntentSource.Human, ProposedName = "Prio",
            Namespace = new NamespaceRef("myNative"), Kind = TypeKind.Enum,
        });
        var struct_ = CppCliRefactorEngine.AddGhostType(Target(), new AddGhostTypeIntent
        {
            Source = IntentSource.Human, ProposedName = "Rect",
            Namespace = new NamespaceRef("myNative"), Kind = TypeKind.Struct,
        });

        Assert.Contains("public interface class IFoo", iface.Single(c => c.Kind == DocumentChangeKind.Added).NewText!);
        Assert.Contains("public enum class Prio { }", enum_.Single(c => c.Kind == DocumentChangeKind.Added).NewText!);
        Assert.Contains("public value struct Rect", struct_.Single(c => c.Kind == DocumentChangeKind.Added).NewText!);
    }

    [Fact]
    public void Rename_replaces_whole_word_across_all_source_files()
    {
        File.WriteAllText(Path.Combine(_projectDir, "MyClass.h"),
            "namespace n { public ref class MyClass { void MyClass_helper(); }; }");
        File.WriteAllText(Path.Combine(_projectDir, "MyClass.cpp"),
            "#include \"MyClass.h\"\nvoid n::MyClass::MyClass_helper() { MyClass^ x; }");
        // Ensure sub-string collisions are NOT replaced.
        File.WriteAllText(Path.Combine(_projectDir, "Other.h"),
            "namespace n { public ref class MyClassExtra { }; }");

        var changes = CppCliRefactorEngine.Rename(Target(), "MyClass", "Widget");

        // MyClass.h and MyClass.cpp both changed; Other.h left alone (MyClassExtra has no whole-word match)
        Assert.Equal(2, changes.Count);
        var header = changes.Single(c => c.FilePath.EndsWith("MyClass.h", StringComparison.Ordinal));
        Assert.Contains("public ref class Widget", header.NewText!);
        // Note: `MyClass_helper` has `MyClass` as a prefix but `_` sits inside the word,
        // so \b does NOT match between them and `MyClass_helper` stays unchanged.
        Assert.Contains("MyClass_helper", header.NewText!);

        var cpp = changes.Single(c => c.FilePath.EndsWith("MyClass.cpp", StringComparison.Ordinal));
        Assert.Contains("n::Widget::MyClass_helper", cpp.NewText!);
        Assert.Contains("Widget^ x", cpp.NewText!);

        Assert.DoesNotContain(changes, c => c.FilePath.EndsWith("Other.h", StringComparison.Ordinal));
    }

    [Fact]
    public void Rename_no_op_when_name_not_present()
    {
        File.WriteAllText(Path.Combine(_projectDir, "Foo.h"), "namespace n { public ref class Foo { }; }");
        var changes = CppCliRefactorEngine.Rename(Target(), "Bar", "Baz");
        Assert.Empty(changes);
    }

    [Fact]
    public void ExtractInterface_adds_header_updates_base_list_and_vcxproj()
    {
        var headerPath = Path.Combine(_projectDir, "Widget.h");
        File.WriteAllText(headerPath,
            """
            #pragma once
            namespace n {
                public ref class Widget
                {
                public:
                    void Reset();
                    property bool IsOn;
                    int Field;
                };
            }
            """);

        var typeRef = new TypeRef("n.Widget");
        var intent = new ExtractInterfaceIntent
        {
            Source = IntentSource.Human,
            SourceType = typeRef,
            ProposedInterfaceName = "IWidget",
            Members = new[]
            {
                new MemberRef(typeRef, "Reset()"),
                new MemberRef(typeRef, "IsOn"),
                new MemberRef(typeRef, "Field"),
            },
        };

        var changes = CppCliRefactorEngine.ExtractInterface(Target(), intent, "Widget");

        var added = changes.Single(c => c.Kind == DocumentChangeKind.Added);
        Assert.EndsWith("IWidget.h", added.FilePath);
        Assert.Contains("public interface class IWidget", added.NewText!);
        Assert.Contains("void Reset();", added.NewText!);
        Assert.Contains("property bool IsOn;", added.NewText!);
        Assert.DoesNotContain("Field", added.NewText!); // Fields are not part of interfaces

        var headerMod = changes.Single(c => Path.GetFileName(c.FilePath) == "Widget.h");
        Assert.Contains("class Widget : public IWidget", headerMod.NewText!);

        var vcxprojMod = changes.Single(c => c.FilePath.EndsWith("myNative.vcxproj", StringComparison.Ordinal));
        Assert.Contains("<ClInclude Include=\"IWidget.h\"", vcxprojMod.NewText!);
    }

    [Fact]
    public void ExtractInterface_generated_method_carries_parameter_list()
    {
        var headerPath = Path.Combine(_projectDir, "Widget.h");
        File.WriteAllText(headerPath,
            """
            #pragma once
            namespace n {
                public ref class Widget
                {
                public:
                    int Add(int x, int y);
                    void Notify(System::Action^ cb);
                };
            }
            """);

        var typeRef = new TypeRef("n.Widget");
        var intent = new ExtractInterfaceIntent
        {
            Source = IntentSource.Human,
            SourceType = typeRef,
            ProposedInterfaceName = "IWidget",
            Members = new[]
            {
                new MemberRef(typeRef, "Add()"),
                new MemberRef(typeRef, "Notify()"),
            },
        };

        var changes = CppCliRefactorEngine.ExtractInterface(Target(), intent, "Widget");
        var added = changes.Single(c => c.Kind == DocumentChangeKind.Added);

        Assert.Contains("int Add(int x, int y);", added.NewText!);
        Assert.Contains("void Notify(System :: Action ^ cb);", added.NewText!);
    }

    [Fact]
    public void ExtractInterface_appends_when_source_already_has_base_list()
    {
        var headerPath = Path.Combine(_projectDir, "Widget.h");
        File.WriteAllText(headerPath,
            """
            #pragma once
            namespace n {
                ref class Base;
                public ref class Widget : public Base
                {
                public:
                    void M();
                };
            }
            """);

        var typeRef = new TypeRef("n.Widget");
        var intent = new ExtractInterfaceIntent
        {
            Source = IntentSource.Human,
            SourceType = typeRef,
            ProposedInterfaceName = "IWidget",
            Members = new[] { new MemberRef(typeRef, "M()") },
        };

        var changes = CppCliRefactorEngine.ExtractInterface(Target(), intent, "Widget");
        var headerMod = changes.Single(c => Path.GetFileName(c.FilePath) == "Widget.h");

        // Must insert IWidget BEFORE the existing base, preserving it.
        Assert.Contains("class Widget : public IWidget, public Base", headerMod.NewText!);
    }

    [Fact]
    public void ExtractSuperclass_creates_abstract_header_and_wires_base_list()
    {
        var headerPath = Path.Combine(_projectDir, "Widget.h");
        File.WriteAllText(headerPath,
            """
            #pragma once
            namespace n {
                public ref class Widget
                {
                public:
                    void Reset();
                    int Compute(int x);
                };
            }
            """);

        var typeRef = new TypeRef("n.Widget");
        var intent = new ExtractSuperclassIntent
        {
            Source = IntentSource.Human,
            SourceType = typeRef,
            ProposedSuperclassName = "WidgetBase",
            Members = new[]
            {
                new MemberRef(typeRef, "Reset()"),
                new MemberRef(typeRef, "Compute()"),
            },
        };

        var changes = CppCliRefactorEngine.ExtractSuperclass(Target(), intent, "Widget");

        var added = changes.Single(c => c.Kind == DocumentChangeKind.Added);
        Assert.EndsWith("WidgetBase.h", added.FilePath);
        Assert.Contains("public ref class WidgetBase abstract", added.NewText!);
        Assert.Contains("virtual void Reset();", added.NewText!);
        Assert.Contains("virtual int Compute(int x);", added.NewText!);

        var headerMod = changes.Single(c => Path.GetFileName(c.FilePath) == "Widget.h");
        Assert.Contains("class Widget : public WidgetBase", headerMod.NewText!);

        var vcxprojMod = changes.Single(c => c.FilePath.EndsWith("myNative.vcxproj", StringComparison.Ordinal));
        Assert.Contains("<ClInclude Include=\"WidgetBase.h\"", vcxprojMod.NewText!);
    }

    [Fact]
    public void ExtractClass_creates_new_class_and_adds_delegate_field()
    {
        var headerPath = Path.Combine(_projectDir, "Person.h");
        File.WriteAllText(headerPath,
            """
            #pragma once
            namespace n {
                public ref class Person
                {
                public:
                    void Name();
                    void OfficeAreaCode();
                    void OfficeNumber();
                };
            }
            """);

        var typeRef = new TypeRef("n.Person");
        var intent = new ExtractClassIntent
        {
            Source = IntentSource.Human,
            SourceType = typeRef,
            ProposedClassName = "TelephoneNumber",
            DelegatePropertyName = "Telephone",
            Members = new[]
            {
                new MemberRef(typeRef, "OfficeAreaCode()"),
                new MemberRef(typeRef, "OfficeNumber()"),
            },
        };

        var changes = CppCliRefactorEngine.ExtractClass(Target(), intent, "Person");

        var added = changes.Single(c => c.Kind == DocumentChangeKind.Added);
        Assert.EndsWith("TelephoneNumber.h", added.FilePath);
        Assert.Contains("public ref class TelephoneNumber", added.NewText!);
        Assert.DoesNotContain("abstract", added.NewText!);

        var headerMod = changes.Single(c => Path.GetFileName(c.FilePath) == "Person.h");
        Assert.Contains("TelephoneNumber^ Telephone;", headerMod.NewText!);
        // Base list must NOT be modified for Extract Class (delegation, not inheritance).
        Assert.DoesNotContain(": public TelephoneNumber", headerMod.NewText!);

        var vcxprojMod = changes.Single(c => c.FilePath.EndsWith("myNative.vcxproj", StringComparison.Ordinal));
        Assert.Contains("<ClInclude Include=\"TelephoneNumber.h\"", vcxprojMod.NewText!);
    }

    [Fact]
    public void RemoveSubclass_deletes_defining_header_and_rewrites_usages()
    {
        var circlePath = Path.Combine(_projectDir, "Circle.h");
        File.WriteAllText(circlePath,
            """
            #pragma once
            #include "Shape.h"
            namespace n {
                public ref class Circle : public Shape {};
            }
            """);
        File.WriteAllText(Path.Combine(_projectDir, "Shape.h"),
            """
            #pragma once
            namespace n {
                public ref class Shape {};
            }
            """);
        var registryPath = Path.Combine(_projectDir, "Registry.h");
        File.WriteAllText(registryPath,
            """
            #pragma once
            #include "Circle.h"
            namespace n {
                public ref class Registry {
                public:
                    Circle^ MakeCircle();
                };
            }
            """);

        var changes = CppCliRefactorEngine.RemoveSubclass(Target(), "Circle", "Shape");

        var deleted = changes.Single(c => c.Kind == DocumentChangeKind.Deleted);
        Assert.EndsWith("Circle.h", deleted.FilePath);

        var modified = changes.Single(c => c.Kind == DocumentChangeKind.Modified
                                        && Path.GetFileName(c.FilePath) == "Registry.h");
        Assert.Contains("Shape^ MakeCircle();", modified.NewText!);
        // Whole-word only: "MakeCircle" keeps its trailing "Circle".
        Assert.DoesNotContain("Circle^", modified.NewText!);

        // Shape.h itself is untouched (no `\bCircle\b` occurrence in it).
        Assert.DoesNotContain(changes, c => Path.GetFileName(c.FilePath) == "Shape.h");
    }

    [Fact]
    public void CollapseHierarchy_pulls_members_up_deletes_subclass_rewrites_usages()
    {
        File.WriteAllText(Path.Combine(_projectDir, "Employee.h"),
            """
            #pragma once
            namespace n {
                public ref class Employee
                {
                public:
                    void Name();
                };
            }
            """);
        var salariedPath = Path.Combine(_projectDir, "SalariedEmployee.h");
        File.WriteAllText(salariedPath,
            """
            #pragma once
            #include "Employee.h"
            namespace n {
                public ref class SalariedEmployee : public Employee
                {
                public:
                    void Salary();
                    void Bonus();
                };
            }
            """);
        var payrollPath = Path.Combine(_projectDir, "Payroll.h");
        File.WriteAllText(payrollPath,
            """
            #pragma once
            #include "SalariedEmployee.h"
            namespace n {
                public ref class Payroll {
                public:
                    SalariedEmployee^ CreateStaff();
                };
            }
            """);

        var changes = CppCliRefactorEngine.CollapseHierarchy(Target(), "SalariedEmployee", "Employee");

        var employeeMod = changes.Single(c => Path.GetFileName(c.FilePath) == "Employee.h");
        Assert.Contains("virtual void Salary();", employeeMod.NewText!);
        Assert.Contains("virtual void Bonus();", employeeMod.NewText!);
        Assert.Contains("void Name();", employeeMod.NewText!); // original kept

        var deleted = changes.Single(c => c.Kind == DocumentChangeKind.Deleted);
        Assert.EndsWith("SalariedEmployee.h", deleted.FilePath);

        var payrollMod = changes.Single(c => Path.GetFileName(c.FilePath) == "Payroll.h");
        Assert.Contains("Employee^ CreateStaff();", payrollMod.NewText!);
    }

    [Fact]
    public void MoveMembersBetweenClasses_pulls_method_up_from_sub_to_parent()
    {
        File.WriteAllText(Path.Combine(_projectDir, "Employee.h"),
            """
            #pragma once
            namespace n {
                public ref class Employee
                {
                public:
                    void Name();
                };
            }
            """);
        File.WriteAllText(Path.Combine(_projectDir, "SalariedEmployee.h"),
            """
            #pragma once
            #include "Employee.h"
            namespace n {
                public ref class SalariedEmployee : public Employee
                {
                public:
                    void Salary();
                    void Bonus();
                };
            }
            """);

        var changes = CppCliRefactorEngine.MoveMembersBetweenClasses(
            Target(), "SalariedEmployee", "Employee", new[] { "Bonus" });

        var employeeMod = changes.Single(c => Path.GetFileName(c.FilePath) == "Employee.h");
        Assert.Contains("virtual void Bonus();", employeeMod.NewText!);
        Assert.Contains("void Name();", employeeMod.NewText!);

        var subMod = changes.Single(c => Path.GetFileName(c.FilePath) == "SalariedEmployee.h");
        Assert.DoesNotContain("Bonus", subMod.NewText!);
        Assert.Contains("void Salary();", subMod.NewText!);
    }

    [Fact]
    public void MoveMembersBetweenClasses_pushes_method_down_from_parent_to_sub()
    {
        File.WriteAllText(Path.Combine(_projectDir, "Employee.h"),
            """
            #pragma once
            namespace n {
                public ref class Employee
                {
                public:
                    void Name();
                    void QuotaBonus();
                };
            }
            """);
        File.WriteAllText(Path.Combine(_projectDir, "SalariedEmployee.h"),
            """
            #pragma once
            #include "Employee.h"
            namespace n {
                public ref class SalariedEmployee : public Employee
                {
                public:
                    void Salary();
                };
            }
            """);

        var changes = CppCliRefactorEngine.MoveMembersBetweenClasses(
            Target(), "Employee", "SalariedEmployee", new[] { "QuotaBonus" });

        var employeeMod = changes.Single(c => Path.GetFileName(c.FilePath) == "Employee.h");
        Assert.DoesNotContain("QuotaBonus", employeeMod.NewText!);
        Assert.Contains("void Name();", employeeMod.NewText!);

        var subMod = changes.Single(c => Path.GetFileName(c.FilePath) == "SalariedEmployee.h");
        Assert.Contains("virtual void QuotaBonus();", subMod.NewText!);
        Assert.Contains("void Salary();", subMod.NewText!);
    }

    [Fact]
    public void MoveMembersBetweenClasses_pulls_field_up()
    {
        File.WriteAllText(Path.Combine(_projectDir, "Employee.h"),
            """
            #pragma once
            namespace n {
                public ref class Employee
                {
                public:
                    System::String^ Name;
                };
            }
            """);
        File.WriteAllText(Path.Combine(_projectDir, "SalariedEmployee.h"),
            """
            #pragma once
            #include "Employee.h"
            namespace n {
                public ref class SalariedEmployee : public Employee
                {
                public:
                    double AnnualSalary;
                };
            }
            """);

        var changes = CppCliRefactorEngine.MoveMembersBetweenClasses(
            Target(), "SalariedEmployee", "Employee", new[] { "AnnualSalary" });

        var employeeMod = changes.Single(c => Path.GetFileName(c.FilePath) == "Employee.h");
        Assert.Contains("double AnnualSalary;", employeeMod.NewText!);

        var subMod = changes.Single(c => Path.GetFileName(c.FilePath) == "SalariedEmployee.h");
        Assert.DoesNotContain("AnnualSalary", subMod.NewText!);
    }

    [Fact]
    public void RemoveSettingMethod_deletes_setter_prefixed_method()
    {
        File.WriteAllText(Path.Combine(_projectDir, "Order.h"),
            """
            #pragma once
            namespace n {
                public ref class Order
                {
                public:
                    int Id();
                    void SetId(int value);
                    double Total();
                };
            }
            """);

        var changes = CppCliRefactorEngine.RemoveSettingMethod(Target(), "Order", "Id");

        var mod = Assert.Single(changes);
        Assert.Equal(DocumentChangeKind.Modified, mod.Kind);
        Assert.DoesNotContain("SetId", mod.NewText!);
        Assert.Contains("int Id();", mod.NewText!);
        Assert.Contains("double Total();", mod.NewText!);
    }

    [Fact]
    public void PullUpConstructorBody_moves_statements_from_sub_ctor_to_parent_ctor()
    {
        File.WriteAllText(Path.Combine(_projectDir, "Employee.h"),
            """
            #pragma once
            namespace n {
                public ref class Employee
                {
                public:
                    Employee() { Name = "base"; }
                    System::String^ Name;
                };
            }
            """);
        File.WriteAllText(Path.Combine(_projectDir, "Manager.h"),
            """
            #pragma once
            #include "Employee.h"
            namespace n {
                public ref class Manager : public Employee
                {
                public:
                    Manager() { TeamSize = 3; }
                    int TeamSize;
                };
            }
            """);

        var changes = CppCliRefactorEngine.PullUpConstructorBody(Target(), "Manager", "Employee");

        var employeeMod = changes.Single(c => Path.GetFileName(c.FilePath) == "Employee.h");
        // Parent constructor body now also contains the pulled statement.
        Assert.Contains("TeamSize = 3", employeeMod.NewText!);
        Assert.Contains("Name = \"base\"", employeeMod.NewText!);

        var subMod = changes.Single(c => Path.GetFileName(c.FilePath) == "Manager.h");
        // Sub ctor body is empty and delegates to parent via `: Employee()`.
        Assert.Contains(": Employee()", subMod.NewText!);
        Assert.DoesNotContain("TeamSize = 3", subMod.NewText!);
    }

    [Fact]
    public void Rename_field_style_replaces_field_and_usage_whole_word()
    {
        // RenameFieldIntent is dispatched via CppCliRefactorEngine.Rename in the adapter.
        // Verify the underlying behaviour still works for a lowercase field identifier.
        File.WriteAllText(Path.Combine(_projectDir, "Order.h"),
            """
            #pragma once
            namespace n {
                public ref class Order
                {
                public:
                    double totalAmount;
                    double ComputeDiscount() { return totalAmount * 0.1; }
                };
            }
            """);

        var changes = CppCliRefactorEngine.Rename(Target(), "totalAmount", "TotalAmount");

        var mod = changes.Single(c => Path.GetFileName(c.FilePath) == "Order.h");
        Assert.Contains("double TotalAmount;", mod.NewText!);
        Assert.Contains("TotalAmount * 0.1", mod.NewText!);
        Assert.DoesNotContain("totalAmount", mod.NewText!);
    }

    [Fact]
    public void EncapsulateField_converts_public_field_to_property_declaration()
    {
        File.WriteAllText(Path.Combine(_projectDir, "Order.h"),
            """
            #pragma once
            namespace n {
                public ref class Order
                {
                public:
                    double Total;
                    System::String^ CustomerName;
                };
            }
            """);

        var changes = CppCliRefactorEngine.EncapsulateField(Target(), "Order", "Total");

        var mod = Assert.Single(changes);
        Assert.Contains("property double Total;", mod.NewText!);
        // The sibling field is untouched.
        Assert.Contains("System::String^ CustomerName;", mod.NewText!);
        // No lingering plain field decl for Total.
        Assert.DoesNotContain("    double Total;", mod.NewText!);
    }

    [Fact]
    public void ReplaceConstructorWithFactory_inserts_static_factory_that_gcnews_the_type()
    {
        File.WriteAllText(Path.Combine(_projectDir, "Order.h"),
            """
            #pragma once
            namespace n {
                public ref class Order
                {
                public:
                    Order(int id) { Id = id; }
                    int Id;
                };
            }
            """);

        var changes = CppCliRefactorEngine.ReplaceConstructorWithFactory(Target(), "Order", "Create");

        var mod = Assert.Single(changes);
        Assert.Contains("static Order^ Create(int id)", mod.NewText!);
        Assert.Contains("gcnew Order(id)", mod.NewText!);
    }

    [Fact]
    public void ReplaceMagicNumber_adds_const_and_replaces_whole_number_occurrences()
    {
        File.WriteAllText(Path.Combine(_projectDir, "Circle.h"),
            """
            #pragma once
            namespace n {
                public ref class Circle
                {
                public:
                    double Radius;
                    double Circumference() { return Radius * 2 * 3.14159; }
                    double Diameter() { return Radius * 2; }
                };
            }
            """);

        var changes = CppCliRefactorEngine.ReplaceMagicNumber(Target(), "Circle", "3.14159", "Pi", "double");

        var mod = Assert.Single(changes);
        Assert.Contains("static const double Pi = 3.14159;", mod.NewText!);
        Assert.Contains("Radius * 2 * Pi", mod.NewText!);
        Assert.Contains("Radius * 2;", mod.NewText!);
    }

    [Fact]
    public void RemoveFieldFromClass_drops_only_the_named_field_line()
    {
        File.WriteAllText(Path.Combine(_projectDir, "Order.h"),
            """
            #pragma once
            namespace n {
                public ref class Order
                {
                public:
                    System::String^ Id;
                    Customer^ Owner;
                    double Total;
                };
            }
            """);

        var changes = CppCliRefactorEngine.RemoveFieldFromClass(Target(), "Order", "Owner");

        var mod = Assert.Single(changes);
        Assert.DoesNotContain("Customer^ Owner;", mod.NewText!);
        Assert.Contains("System::String^ Id;", mod.NewText!);
        Assert.Contains("double Total;", mod.NewText!);
    }

    [Fact]
    public void AddParameterToMethod_appends_to_argument_list()
    {
        File.WriteAllText(Path.Combine(_projectDir, "Greeter.h"),
            """
            #pragma once
            namespace n {
                public ref class Greeter
                {
                public:
                    System::String^ Say(System::String^ name);
                };
            }
            """);

        var changes = CppCliRefactorEngine.AddParameterToMethod(
            Target(), "Greeter", "Say", "int times");

        var mod = Assert.Single(changes);
        Assert.Contains("Say(System::String^ name, int times)", mod.NewText!);
    }

    [Fact]
    public void RemoveParameterFromMethod_drops_named_argument()
    {
        File.WriteAllText(Path.Combine(_projectDir, "Greeter.h"),
            """
            #pragma once
            namespace n {
                public ref class Greeter
                {
                public:
                    System::String^ Say(System::String^ name, int times, bool loud);
                };
            }
            """);

        var changes = CppCliRefactorEngine.RemoveParameterFromMethod(
            Target(), "Greeter", "Say", "times");

        var mod = Assert.Single(changes);
        Assert.Contains("Say(System::String^ name, bool loud)", mod.NewText!);
        Assert.DoesNotContain("int times", mod.NewText!);
    }

    [Fact]
    public void RenameParameter_replaces_only_the_named_arg_within_the_signature()
    {
        File.WriteAllText(Path.Combine(_projectDir, "Greeter.h"),
            """
            #pragma once
            namespace n {
                public ref class Greeter
                {
                public:
                    System::String^ Say(System::String^ n);
                };
            }
            """);

        var changes = CppCliRefactorEngine.RenameParameter(Target(), "Greeter", "Say", "n", "name");

        var mod = Assert.Single(changes);
        Assert.Contains("Say(System::String^ name)", mod.NewText!);
    }

    [Fact]
    public void TryFindTargetByNamespace_matches_longest_project_name_prefix()
    {
        var model = new SolutionModel("dummy.slnx", new List<ProjectModel>
        {
            new("csLib", "cs.csproj", "csharp", Array.Empty<TypeModel>()),
            new("myNative", _vcxprojPath, "cpp-cli", Array.Empty<TypeModel>()),
            new("myNativeCore", "other.vcxproj", "cpp-cli", Array.Empty<TypeModel>()),
        });

        Assert.True(CppCliRefactorEngine.TryFindTargetByNamespace(model, new NamespaceRef("myNative"), out var t1));
        Assert.Equal("myNative", t1!.ProjectName);

        Assert.True(CppCliRefactorEngine.TryFindTargetByNamespace(model, new NamespaceRef("myNativeCore.Sub"), out var t2));
        Assert.Equal("myNativeCore", t2!.ProjectName);

        Assert.False(CppCliRefactorEngine.TryFindTargetByNamespace(model, new NamespaceRef("csLib"), out _));
    }
}
