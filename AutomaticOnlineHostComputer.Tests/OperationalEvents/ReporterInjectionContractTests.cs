using System.Reflection;
using System.Text.RegularExpressions;
using AutomaticOnlineHostComputer.Presentation.ViewModels.Home;
using AutomaticOnlineHostComputer.Service;
using AutomaticOnlineHostComputer.Service.OperationalEvents;

namespace AutomaticOnlineHostComputer.Tests.OperationalEvents;

public sealed class ReporterInjectionContractTests
{
    private static readonly Type[] EngineTypes =
    {
        typeof(ProductionFlowEngine),
        typeof(Line1FrontFlowEngine),
        typeof(Line2FrontFlowEngine),
        typeof(Line1RearFlowEngine),
        typeof(Line2RearFlowEngine),
        typeof(BalancingFlowEngine),
        typeof(GrindingFlowEngine)
    };

    [Fact]
    public void Engines_have_one_required_reporter_parameter_and_private_readonly_field()
    {
        foreach (Type engineType in EngineTypes)
        {
            ConstructorInfo constructor = Assert.Single(engineType.GetConstructors(BindingFlags.Instance | BindingFlags.Public));
            ParameterInfo reporterParameter = Assert.Single(
                constructor.GetParameters(),
                parameter => parameter.ParameterType == typeof(IOperationalEventReporter));

            Assert.False(reporterParameter.IsOptional);

            FieldInfo? reporterField = engineType.GetField(
                "_exceptionReporter",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(reporterField);
            Assert.True(reporterField.IsPrivate);
            Assert.True(reporterField.IsInitOnly);
            Assert.Equal(typeof(IOperationalEventReporter), reporterField.FieldType);
        }
    }

    [Fact]
    public void Home_receives_and_stores_only_the_injected_reporter_interface()
    {
        ConstructorInfo constructor = Assert.Single(
            typeof(HomeViewModel).GetConstructors(BindingFlags.Instance | BindingFlags.Public));
        ParameterInfo reporterParameter = Assert.Single(
            constructor.GetParameters(),
            parameter => parameter.ParameterType == typeof(IOperationalEventReporter));
        Assert.False(reporterParameter.IsOptional);

        FieldInfo? reporterField = typeof(HomeViewModel).GetField(
            "_exceptionReporter",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(reporterField);
        Assert.True(reporterField.IsPrivate);
        Assert.True(reporterField.IsInitOnly);
        Assert.Equal(typeof(IOperationalEventReporter), reporterField.FieldType);

        string source = ReadSource("Presentation", "ViewModels", "Home", "HomeViewModel.cs");
        Assert.Contains("_exceptionReporter = exceptionReporter;", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Home_passes_the_same_reporter_field_once_to_each_engine_construction()
    {
        string source = ReadSource("Presentation", "ViewModels", "Home", "HomeViewModel.cs");

        foreach (Type engineType in EngineTypes)
        {
            string arguments = Assert.Single(ExtractConstructorArguments(source, engineType.Name));
            Assert.Single(Regex.Matches(arguments, @"\b_exceptionReporter\b").Cast<Match>());
        }
    }

    [Fact]
    public void Home_and_engines_do_not_construct_reporter_or_store()
    {
        string[] relativePaths =
        {
            Path.Combine("Presentation", "ViewModels", "Home", "HomeViewModel.cs"),
            Path.Combine("Service", "FlowEngine", "ProductionFlowEngine.cs"),
            Path.Combine("Service", "FlowEngine", "Line1FrontFlowEngine.cs"),
            Path.Combine("Service", "FlowEngine", "Line2FrontFlowEngine.cs"),
            Path.Combine("Service", "FlowEngine", "Line1RearFlowEngine.cs"),
            Path.Combine("Service", "FlowEngine", "Line2RearFlowEngine.cs"),
            Path.Combine("Service", "FlowEngine", "BalancingFlowEngine.cs"),
            Path.Combine("Service", "FlowEngine", "GrindingFlowEngine.cs")
        };

        foreach (string relativePath in relativePaths)
        {
            string source = File.ReadAllText(Path.Combine(RepositoryRoot.Find(), relativePath));
            Assert.DoesNotContain("new OperationalEventReporter", source, StringComparison.Ordinal);
            Assert.DoesNotContain("new OperationalEventStore", source, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Operational_event_core_does_not_depend_on_communication_or_device_services()
    {
        string coreDirectory = Path.Combine(RepositoryRoot.Find(), "Service", "OperationalEvents");
        string source = string.Join(
            Environment.NewLine,
            Directory.GetFiles(coreDirectory, "*.cs", SearchOption.AllDirectories)
                .OrderBy(path => path, StringComparer.Ordinal)
                .Select(File.ReadAllText));

        Assert.DoesNotMatch(
            new Regex(@"^\s*using\s+(?:global\s+)?AutomaticOnlineHostComputer\.Communication(?:\.|\s*;)", RegexOptions.Multiline),
            source);
        Assert.DoesNotMatch(
            new Regex(@"\bAutomaticOnlineHostComputer\.Communication\.(?:Clients|DeviceServices|DeviceAddresses|Models)\b"),
            source);
        Assert.DoesNotMatch(
            new Regex(@"\b(?:CraneService|ManipulatorService|McConnectionCache|ManagementQueryService)\b"),
            source);
    }

    private static string ReadSource(params string[] segments) =>
        File.ReadAllText(Path.Combine(new[] { RepositoryRoot.Find() }.Concat(segments).ToArray()));

    private static IReadOnlyList<string> ExtractConstructorArguments(string source, string typeName)
    {
        string marker = $"new {typeName}(";
        var results = new List<string>();
        int searchFrom = 0;

        while (true)
        {
            int markerIndex = source.IndexOf(marker, searchFrom, StringComparison.Ordinal);
            if (markerIndex < 0)
                return results;

            int openParenthesis = markerIndex + marker.Length - 1;
            int closeParenthesis = FindMatchingParenthesis(source, openParenthesis);
            results.Add(source[(openParenthesis + 1)..closeParenthesis]);
            searchFrom = closeParenthesis + 1;
        }
    }

    private static int FindMatchingParenthesis(string source, int openParenthesis)
    {
        int depth = 0;
        bool inString = false;
        bool inCharacter = false;
        bool inLineComment = false;
        bool inBlockComment = false;

        for (int index = openParenthesis; index < source.Length; index++)
        {
            char current = source[index];
            char next = index + 1 < source.Length ? source[index + 1] : '\0';

            if (inLineComment)
            {
                if (current == '\n') inLineComment = false;
                continue;
            }

            if (inBlockComment)
            {
                if (current == '*' && next == '/')
                {
                    inBlockComment = false;
                    index++;
                }
                continue;
            }

            if (!inString && !inCharacter && current == '/' && next == '/')
            {
                inLineComment = true;
                index++;
                continue;
            }

            if (!inString && !inCharacter && current == '/' && next == '*')
            {
                inBlockComment = true;
                index++;
                continue;
            }

            if (!inCharacter && current == '"' && !IsEscaped(source, index))
            {
                inString = !inString;
                continue;
            }

            if (!inString && current == '\'' && !IsEscaped(source, index))
            {
                inCharacter = !inCharacter;
                continue;
            }

            if (inString || inCharacter)
                continue;

            if (current == '(')
                depth++;
            else if (current == ')' && --depth == 0)
                return index;
        }

        throw new InvalidDataException($"未找到位置 {openParenthesis} 对应的右括号。");
    }

    private static bool IsEscaped(string source, int index)
    {
        int slashCount = 0;
        for (int current = index - 1; current >= 0 && source[current] == '\\'; current--)
            slashCount++;
        return slashCount % 2 != 0;
    }
}
