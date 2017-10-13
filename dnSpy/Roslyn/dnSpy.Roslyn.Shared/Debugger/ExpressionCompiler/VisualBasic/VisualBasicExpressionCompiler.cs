﻿/*
    Copyright (C) 2014-2017 de4dot@gmail.com

    This file is part of dnSpy

    dnSpy is free software: you can redistribute it and/or modify
    it under the terms of the GNU General Public License as published by
    the Free Software Foundation, either version 3 of the License, or
    (at your option) any later version.

    dnSpy is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with dnSpy.  If not, see <http://www.gnu.org/licenses/>.
*/

using System;
using System.Collections.Immutable;
using System.Threading;
using dnlib.DotNet;
using dnSpy.Contracts.Debugger.CallStack;
using dnSpy.Contracts.Debugger.DotNet.Evaluation;
using dnSpy.Contracts.Debugger.DotNet.Evaluation.ExpressionCompiler;
using dnSpy.Contracts.Debugger.DotNet.Text;
using dnSpy.Contracts.Debugger.Evaluation;
using dnSpy.Contracts.Decompiler;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.ExpressionEvaluator;
using Microsoft.CodeAnalysis.ExpressionEvaluator.DnSpy;
using Microsoft.CodeAnalysis.VisualBasic;
using Microsoft.CodeAnalysis.VisualBasic.ExpressionEvaluator;
using Microsoft.VisualStudio.Debugger.Evaluation;

namespace dnSpy.Roslyn.Shared.Debugger.ExpressionCompiler.VisualBasic {
	[ExportDbgDotNetExpressionCompiler(DbgDotNetLanguageGuids.VisualBasic, PredefinedDbgLanguageNames.VisualBasic, "Visual Basic", PredefinedDecompilerGuids.VisualBasic, PredefinedDbgDotNetExpressionCompilerOrders.VisualBasic)]
	sealed class VisualBasicExpressionCompiler : LanguageExpressionCompiler {
		sealed class VisualBasicEvalContextState : EvalContextState {
			public VisualBasicMetadataContext MetadataContext;
		}

		protected override ImmutableArray<ImmutableArray<DSEEImportRecord>> GetImports(TypeDef declaringType, MethodDebugScope scope, out string defaultNamespaceName) {
			var fileLevelBuilder = ImmutableArray.CreateBuilder<DSEEImportRecord>(scope.Imports.Length);
			var projectLevelBuilder = ImmutableArray.CreateBuilder<DSEEImportRecord>(scope.Imports.Length);
			defaultNamespaceName = null;
			foreach (var info in scope.Imports) {
				var builder = info.VBImportScopeKind == VBImportScopeKind.Project ? projectLevelBuilder : fileLevelBuilder;
				AddDSEEImportRecord(builder, info, ref defaultNamespaceName);
			}
			if (defaultNamespaceName == null)
				defaultNamespaceName = string.Empty;
			return ImmutableArray.Create(fileLevelBuilder.ToImmutable(), projectLevelBuilder.ToImmutable());
		}

		public override DbgDotNetCompilationResult CompileAssignment(DbgEvaluationContext context, DbgStackFrame frame, DbgModuleReference[] references, DbgDotNetAlias[] aliases, string target, string expression, DbgEvaluationOptions options, CancellationToken cancellationToken) {
			GetCompilationState<VisualBasicEvalContextState>(context, frame, references, out var langDebugInfo, out var method, out var localVarSigTok, out var state, out var metadataBlocks, out var methodVersion);

			var getMethodDebugInfo = CreateGetMethodDebugInfo(state, langDebugInfo);
			var evalCtx = EvaluationContext.CreateMethodContext(state.MetadataContext, metadataBlocks, null, getMethodDebugInfo, method.Module.Mvid ?? Guid.Empty, method.MDToken.ToInt32(), methodVersion, langDebugInfo.ILOffset, localVarSigTok);
			state.MetadataContext = new VisualBasicMetadataContext(metadataBlocks, evalCtx);

			var compileResult = evalCtx.CompileAssignment(target, expression, CreateAliases(aliases), out var resultProperties, out var errorMessage);
			return CreateCompilationResult(target, compileResult, resultProperties, errorMessage, DbgDotNetText.Empty);
		}

		public override DbgDotNetCompilationResult CompileGetLocals(DbgEvaluationContext context, DbgStackFrame frame, DbgModuleReference[] references, DbgEvaluationOptions options, CancellationToken cancellationToken) {
			GetCompilationState<VisualBasicEvalContextState>(context, frame, references, out var langDebugInfo, out var method, out var localVarSigTok, out var state, out var metadataBlocks, out var methodVersion);

			var getMethodDebugInfo = CreateGetMethodDebugInfo(state, langDebugInfo);
			var evalCtx = EvaluationContext.CreateMethodContext(state.MetadataContext, metadataBlocks, null, getMethodDebugInfo, method.Module.Mvid ?? Guid.Empty, method.MDToken.ToInt32(), methodVersion, langDebugInfo.ILOffset, localVarSigTok);
			state.MetadataContext = new VisualBasicMetadataContext(metadataBlocks, evalCtx);

			var asmBytes = evalCtx.CompileGetLocals(false, ImmutableArray<Alias>.Empty, out var localsInfo, out var typeName, out var errorMessage);
			return CreateCompilationResult(state, asmBytes, typeName, localsInfo, errorMessage);
		}

		public override DbgDotNetCompilationResult CompileExpression(DbgEvaluationContext context, DbgStackFrame frame, DbgModuleReference[] references, DbgDotNetAlias[] aliases, string expression, DbgEvaluationOptions options, CancellationToken cancellationToken) {
			GetCompilationState<VisualBasicEvalContextState>(context, frame, references, out var langDebugInfo, out var method, out var localVarSigTok, out var state, out var metadataBlocks, out var methodVersion);

			var getMethodDebugInfo = CreateGetMethodDebugInfo(state, langDebugInfo);
			var evalCtx = EvaluationContext.CreateMethodContext(state.MetadataContext, metadataBlocks, null, getMethodDebugInfo, method.Module.Mvid ?? Guid.Empty, method.MDToken.ToInt32(), methodVersion, langDebugInfo.ILOffset, localVarSigTok);
			state.MetadataContext = new VisualBasicMetadataContext(metadataBlocks, evalCtx);

			var compilationFlags = DkmEvaluationFlags.None;
			if ((options & DbgEvaluationOptions.Expression) != 0)
				compilationFlags |= DkmEvaluationFlags.TreatAsExpression;
			var compileResult = evalCtx.CompileExpression(expression, compilationFlags, CreateAliases(aliases), out var resultProperties, out var errorMessage);
			var name = compileResult == null || errorMessage != null ? CreateErrorName(expression) :
				GetExpressionText(state.MetadataContext.EvaluationContext, state.MetadataContext.Compilation, expression, cancellationToken);
			return CreateCompilationResult(expression, compileResult, resultProperties, errorMessage, name);
		}

		DbgDotNetText GetExpressionText(EvaluationContext evaluationContext, VisualBasicCompilation compilation, string expression, CancellationToken cancellationToken) {
			var (exprSource, exprOffset) = CreateExpressionSource(evaluationContext, expression);
			return GetExpressionText(LanguageNames.VisualBasic, visualBasicCompilationOptions, visualBasicParseOptions, expression, exprSource, exprOffset, compilation.References, cancellationToken);
		}
		static readonly VisualBasicCompilationOptions visualBasicCompilationOptions = new VisualBasicCompilationOptions(OutputKind.DynamicallyLinkedLibrary);
		static readonly VisualBasicParseOptions visualBasicParseOptions = new VisualBasicParseOptions(LanguageVersion.Latest);

		(string exprSource, int exprOffset) CreateExpressionSource(EvaluationContext evaluationContext, string expression) {
			var sb = Formatters.ObjectCache.AllocStringBuilder();

			evaluationContext.WriteImports(sb);
			sb.AppendLine(@"Public Class __C__L__A__S__S__");
			sb.Append(@"Shared Sub __M__E__T__H__O__D__(");
			evaluationContext.WriteParameters(sb);
			sb.AppendLine(")");
			evaluationContext.WriteLocals(sb);
			// Some expressions must be assigned to a variable or we won't get colorized text, eg.
			//		"1234".Length
			//		New System.Collections.Generic.List(Of Integer)()
			sb.Append("Dim __L_O_C_A_L__ = ");
			int exprOffset = sb.Length;
			sb.AppendLine(expression);
			sb.AppendLine("End Sub");
			sb.AppendLine("End Class");

			var exprSource = Formatters.ObjectCache.FreeAndToString(ref sb);
			return (exprSource, exprOffset);
		}
	}
}