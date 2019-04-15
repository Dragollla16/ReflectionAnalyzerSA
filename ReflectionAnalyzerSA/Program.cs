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
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.CodeAnalysis.Text;

namespace ReflectionAnalyzerSA
{
    class Program
    {
        const string ProgramCsText = @"
            using System;

        namespace ActivatorTest
        {
            class Program
            {
        abstract class Base
        {
            public Base()
            {
                Console.WriteLine($""{this.GetType().Name} is instatiated"");
            }
            public Base(object o) { }
        }

        class TypeOfClass : Base { public TypeOfClass() : base() { } }
        class GenericArgumentClass : Base { public GenericArgumentClass() : base() { } }
        class GetTypeClass : Base
        {
            public GetTypeClass() : base() { }
            public GetTypeClass(object o) : base(o) { }
        }

        static void Main(string[] args)
        {
            TryConstruct(typeof(TypeOfClass));
            TryConstruct(new GetTypeClass(null).GetType());
            TryConstruct<GenericArgumentClass>();
            var type = typeof(TypeOfClass);
            Activator.CreateInstance(type);
            Activator.CreateInstance<GenericArgumentClass>();
            Activator.CreateInstance(new GetTypeClass(null).GetType());
            Console.ReadKey();
        }

        static void TryConstruct(Type type)
        {
            try
            {
                Activator.CreateInstance(type);
            } catch(Exception e)
            {
                Console.WriteLine($""Could not instatiate {type.Name} class"");
                Console.WriteLine(e.Message);
            }
        }
        static void TryConstruct<T>()
        {
            try
            {
                Activator.CreateInstance<T>();
            }
            catch (Exception e)
            {
                Console.WriteLine($""Could not instatiate {typeof(T).Name} class"");
                Console.WriteLine(e.Message);
            }
        }
    }
}
";
        
        static async Task Main(string[] args)
        {
            // Attempt to set the version of MSBuild.
            MSBuildLocator.RegisterMSBuildPath(@"/Library/Frameworks/Mono.framework/Versions/Current/Commands/");

            var syntaxTree = CSharpSyntaxTree.ParseText(ProgramCsText);
            var compilation = CSharpCompilation.Create("Test")
                .AddReferences(
                    MetadataReference.CreateFromFile(
                        typeof(object).Assembly.Location), 
                    MetadataReference.CreateFromFile(
                        typeof(Activator).Assembly.Location))
                .AddSyntaxTrees(syntaxTree);
            var semanticModel = compilation.GetSemanticModel(syntaxTree);
            
            var nodes = GetNodes(syntaxTree.GetRoot(), semanticModel, "Activator", "CreateInstance");
            var reservedTypesWithAssemblies = ProcessNodes(nodes, semanticModel).ToList();
            
            Console.WriteLine(CreateLinkerConfiguration(reservedTypesWithAssemblies));
        }
        
        private static IEnumerable<InvocationExpressionSyntax> GetNodes(SyntaxNode root, SemanticModel semanticModel, string className, string methodName) =>
            root.DescendantNodes()
                .OfType<InvocationExpressionSyntax>()
                .Where(IsActivatorCreateInstanceExpression(semanticModel, className, methodName));

        private static Func<InvocationExpressionSyntax, bool> IsActivatorCreateInstanceExpression(SemanticModel semanticModel, string className, string methodName)
        {
            bool DoCheck(InvocationExpressionSyntax expression)
            {
                var s = semanticModel.GetSymbolInfo(expression).Symbol;
                return s.Kind == SymbolKind.Method && s.ContainingType.Name.Equals(className) && s.Name.Equals(methodName);
            }
            return DoCheck;
        }

        private static IEnumerable<(string, string)> ProcessNodes(IEnumerable<InvocationExpressionSyntax> nodes, SemanticModel semanticModel, bool withGeneric = true)
        {
            var reservedTypesWithAssemblies = new HashSet<(string, string)>();
            foreach (var node in nodes)
            {
                if (!withGeneric)
                {
                    var symbol = (IMethodSymbol)semanticModel.GetSymbolInfo(node).Symbol;
                    if (symbol.TypeArguments.Length > 0)
                        continue;
                }
                foreach (var type in GetInvocationsArgsOrigins(node, semanticModel))
                    reservedTypesWithAssemblies.Add(type);
            }
            return reservedTypesWithAssemblies;
        }

        private static IEnumerable<(string, string)> GetInvocationsArgsOrigins(InvocationExpressionSyntax invocation, SemanticModel semanticModel)
        {
            var symbol = (IMethodSymbol)semanticModel.GetSymbolInfo(invocation).Symbol;
            if (symbol.TypeArguments.Length > 0)
                return ProcessGenericTypeCase(semanticModel, symbol);
            if (symbol.Parameters.Length > 0)
                return ProcessTypeAsArgumentCase(invocation, semanticModel);
            throw new NotSupportedException();
        }

        private static IEnumerable<(string, string)> ProcessTypeAsArgumentCase(InvocationExpressionSyntax invocation, SemanticModel semanticModel)
        {
            var typeArg = invocation.ArgumentList.Arguments[0].Expression;
            switch (typeArg)
            {
                case InvocationExpressionSyntax innerInvocation:
                    return new List<(string, string)> { ProcessInnerInvocation(innerInvocation.Expression, semanticModel) };
                case TypeOfExpressionSyntax typeOfExpressionSyntax:
                    return new List<(string, string)> { ProcessInnerInvocation(typeOfExpressionSyntax, semanticModel) };
            }

            var dataFlowAnalysis = semanticModel.AnalyzeDataFlow(typeArg);
            var inFlow = dataFlowAnalysis.DataFlowsIn[0];
            var syntaxInFlow = inFlow.DeclaringSyntaxReferences[0].GetSyntax();
            switch (syntaxInFlow)
            {
                case VariableDeclaratorSyntax declaratorSyntax:
                    return new List<(string, string)>
                        {ProcessInnerInvocation(declaratorSyntax.Initializer.Value, semanticModel)};
                case ParameterSyntax parameterSyntax:
                {
                    var method = (MethodDeclarationSyntax) ((ParameterListSyntax) parameterSyntax.Parent).Parent;
                    var symbol = semanticModel.GetDeclaredSymbol(method);
                    var nodes = GetNodes(semanticModel.SyntaxTree.GetRoot(), semanticModel, symbol.ContainingType.Name,
                        symbol.Name).ToList();
                    return ProcessNodes(nodes, semanticModel, false);
                }
                default:
                    throw new NotSupportedException();
            }
        }

        private static IEnumerable<(string, string)> ProcessGenericTypeCase(SemanticModel semanticModel, IMethodSymbol symbol)
        {
            var constructedType = symbol.TypeArguments[0];
            if (constructedType.Kind != SymbolKind.TypeParameter)
                return new List<(string, string)> { (constructedType.ContainingAssembly.Name, constructedType.Name) };

            var methodSymbol = constructedType.ContainingSymbol;
            var methodName = methodSymbol.Name;
            var className = methodSymbol.ContainingSymbol.Name;

            var nodes = GetNodes(semanticModel.SyntaxTree.GetRoot(), semanticModel, className, methodName)
                .Where(n =>
                {
                    var s = (IMethodSymbol) semanticModel.GetSymbolInfo(n).Symbol;
                    return s.TypeParameters.Contains(constructedType);
                });
            return ProcessNodes(nodes, semanticModel);
        }

        private static (string, string) ProcessInnerInvocation(ExpressionSyntax expressionSyntax, SemanticModel semanticModel)
        {
            switch (expressionSyntax)
            {
                case MemberAccessExpressionSyntax getTypeExp when getTypeExp.Name.Identifier.Text.Equals("GetType"):
                {
                    var type = semanticModel.GetTypeInfo(getTypeExp.Expression).Type;
                    return (type.ContainingAssembly.Name, type.Name);
                }
                case TypeOfExpressionSyntax typeOfExp:
                {
                    var type = semanticModel.GetTypeInfo(typeOfExp.Type).Type;  
                    return (type.ContainingAssembly.Name, type.Name);
                }
                default:
                    throw new NotSupportedException();
            }
        }

        private static string CreateLinkerConfiguration(IEnumerable<(string Assembly, string Type)> reservedTypes)
        {
            var typesDict = new Dictionary<string, List<string>>();
            foreach (var (assembly, type) in reservedTypes)
            {
                if(typesDict.ContainsKey(assembly))
                    typesDict[assembly].Add(type);
                else
                    typesDict.Add(assembly, new List<string> { type });
            }

            const string header = "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<linker>\n\t";
            const string footer = "</linker>";

            var body = "";
            
            foreach (var kv in typesDict)
            {
                var assembly = kv.Key;
                var types = kv.Value;
                var assemblyBlock = $"<assembly fullname=\"{assembly}\">\n\t\t";
                foreach (var type in types)
                    assemblyBlock += $"<type fullname=\"{type}\"/>\n\t\t";
                // remove last \t
                assemblyBlock = assemblyBlock.Substring(0, assemblyBlock.Length - 1);
                assemblyBlock += "</assembly>\n\t";
                body += assemblyBlock;
            }

            body = body.Substring(0, body.Length - 1);

            return header + body + footer;
        }
    }
}
