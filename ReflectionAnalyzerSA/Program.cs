using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Symbols;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.CodeAnalysis.Text;

namespace ReflectionAnalyzerSA
{
    class Program
    {
        static async Task Main(string[] args)
        {
            // Attempt to set the version of MSBuild.
            var visualStudioInstances = MSBuildLocator.QueryVisualStudioInstances().ToArray();
            var instance = visualStudioInstances.Length == 1
                // If there is only one instance of MSBuild on this machine, set that as the one to use.
                ? visualStudioInstances[0]
                // Handle selecting the version of MSBuild you want to use.
                : SelectVisualStudioInstance(visualStudioInstances);

            Console.WriteLine($"Using MSBuild at '{instance.MSBuildPath}' to load projects.");

            // NOTE: Be sure to register an instance with the MSBuildLocator 
            //       before calling MSBuildWorkspace.Create()
            //       otherwise, MSBuildWorkspace won't MEF compose.
            MSBuildLocator.RegisterInstance(instance);

            using (var workspace = MSBuildWorkspace.Create())
            {
                // Print message for WorkspaceFailed event to help diagnosing project load failures.
                workspace.WorkspaceFailed += (o, e) => Console.WriteLine(e.Diagnostic.Message);

                //var projectPath = @"C:\Users\Никита\source\repos\ReflectionAnalyzerSA\ActivatorTest\ActivatorTest.csproj";
                var projectPath = @"..\..\..\..\ActivatorTest\ActivatorTest.csproj";

                // Attach progress reporter so we print projects as they are loaded.
                var project = await workspace.OpenProjectAsync(projectPath);
                Console.WriteLine($"Finished loading project '{projectPath}'");

                // TODO: Do analysis on the projects in the loaded solution
                var programCs = project.Documents.First(d => d.Name.Equals(@"Program.cs"));
                Console.WriteLine($"Open source code file {programCs.Name}");

                var syntaxTree = await programCs.GetSyntaxTreeAsync();
                var semanticModel = await programCs.GetSemanticModelAsync();

                var root = syntaxTree.GetCompilationUnitRoot();

                var nodes = GetCreateInstanceNodes(root)
                    .ToList();
                foreach (var node in nodes)
                    Console.WriteLine(node);
            }
        }

        static IEnumerable<InvocationExpressionSyntax> GetCreateInstanceNodes(CompilationUnitSyntax root)
        {
            return root.DescendantNodes()
                .OfType<InvocationExpressionSyntax>()
                .Where(IsActivatorCreateInstanceExpression);
        }

        static bool IsActivatorCreateInstanceExpression(InvocationExpressionSyntax expression)
        {
            if (expression.Expression is MemberAccessExpressionSyntax memberAccess)
            {
                if (memberAccess.Expression is IdentifierNameSyntax identifierName && !identifierName.Identifier.Text.Equals("Activator"))
                    return false;
                return memberAccess.Name.Identifier.Text.Equals("CreateInstance");
            }
            return false;
        }

        static void PrintNodes(SyntaxNode node, int tabsCount = 0)
        {
            if(!node.ChildNodes().Any())
                Console.WriteLine($"{new string(Enumerable.Repeat('\t', tabsCount).ToArray())}{node}");

            foreach (var n in node.ChildNodes())
                PrintNodes(n, tabsCount + 1);
        }

        private static VisualStudioInstance SelectVisualStudioInstance(VisualStudioInstance[] visualStudioInstances)
        {
            Console.WriteLine("Multiple installs of MSBuild detected please select one:");
            for (int i = 0; i < visualStudioInstances.Length; i++)
            {
                Console.WriteLine($"Instance {i + 1}");
                Console.WriteLine($"    Name: {visualStudioInstances[i].Name}");
                Console.WriteLine($"    Version: {visualStudioInstances[i].Version}");
                Console.WriteLine($"    MSBuild Path: {visualStudioInstances[i].MSBuildPath}");
            }

            while (true)
            {
                var userResponse = Console.ReadLine();
                if (int.TryParse(userResponse, out int instanceNumber) &&
                    instanceNumber > 0 &&
                    instanceNumber <= visualStudioInstances.Length)
                {
                    return visualStudioInstances[instanceNumber - 1];
                }
                Console.WriteLine("Input not accepted, try again.");
            }
        }

        private class ConsoleProgressReporter : IProgress<ProjectLoadProgress>
        {
            public void Report(ProjectLoadProgress loadProgress)
            {
                var projectDisplay = Path.GetFileName(loadProgress.FilePath);
                if (loadProgress.TargetFramework != null)
                {
                    projectDisplay += $" ({loadProgress.TargetFramework})";
                }

                Console.WriteLine($"{loadProgress.Operation,-15} {loadProgress.ElapsedTime,-15:m\\:ss\\.fffffff} {projectDisplay}");
            }
        }
    }
}
