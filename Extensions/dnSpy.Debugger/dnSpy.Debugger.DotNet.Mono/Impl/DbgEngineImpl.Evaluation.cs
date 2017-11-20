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
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using dnSpy.Contracts.Debugger;
using dnSpy.Contracts.Debugger.DotNet.Evaluation;
using dnSpy.Contracts.Debugger.Engine.Evaluation;
using dnSpy.Contracts.Debugger.Evaluation;
using dnSpy.Debugger.DotNet.Metadata;
using dnSpy.Debugger.DotNet.Mono.Impl.Evaluation;
using dnSpy.Debugger.DotNet.Mono.Properties;
using Mono.Debugger.Soft;

namespace dnSpy.Debugger.DotNet.Mono.Impl {
	sealed partial class DbgEngineImpl {
		internal int? OffsetToStringData => objectConstants?.OffsetToStringData;
		internal int? OffsetToArrayData => objectConstants?.OffsetToArrayData;
		ObjectConstants objectConstants;
		bool canInitializeObjectConstants;

		void InitializeObjectConstants_MonoDebug() {
			debuggerThread.VerifyAccess();
			if (!canInitializeObjectConstants)
				return;
			if (objectConstants != null)
				return;
			if (objectFactory == null)
				return;

			foreach (var thread in vm.GetThreads()) {
				if (thread.Name == FinalizerName)
					continue;
				var factory = new ObjectConstantsFactory(objectFactory.Process, thread);
				if (factory.TryCreate(out objectConstants))
					break;
			}
		}

		internal DbgDotNetValue CreateDotNetValue_MonoDebug(DmdAppDomain reflectionAppDomain, Value value) {
			debuggerThread.VerifyAccess();
			if (value == null)
				return new SyntheticNullValue(reflectionAppDomain.System_Object);
			DmdType type;
			if (value is PrimitiveValue pv)
				type = MonoValueTypeCreator.CreateType(this, value, reflectionAppDomain.System_Object);
			else if (value is StructMirror sm)
				type = new ReflectionTypeCreator(this, reflectionAppDomain).Create(sm.Type);
			else {
				Debug.Assert(value is ObjectMirror);
				type = new ReflectionTypeCreator(this, reflectionAppDomain).Create(((ObjectMirror)value).Type);
			}
			var valueLocation = new NoValueLocation(type, value);
			return CreateDotNetValue_MonoDebug(valueLocation);
		}

		internal DbgDotNetValue CreateDotNetValue_MonoDebug(ValueLocation valueLocation) {
			debuggerThread.VerifyAccess();
			var value = valueLocation.Load();
			if (value == null)
				return new SyntheticNullValue(valueLocation.Type);

			var dnValue = new DbgDotNetValueImpl(this, valueLocation, value);
			lock (lockObj)
				dotNetValuesToCloseOnContinue.Add(dnValue);
			return dnValue;
		}

		void CloseDotNetValues_MonoDebug() {
			debuggerThread.VerifyAccess();
			DbgDotNetValueImpl[] valuesToClose;
			lock (lockObj) {
				valuesToClose = dotNetValuesToCloseOnContinue.Count == 0 ? Array.Empty<DbgDotNetValueImpl>() : dotNetValuesToCloseOnContinue.ToArray();
				dotNetValuesToCloseOnContinue.Clear();
			}
			foreach (var value in valuesToClose)
				value.Dispose();
		}

		bool IsEvaluating => funcEvalFactory.IsEvaluating;
		internal int MethodInvokeCounter => funcEvalFactory.MethodInvokeCounter;

		internal TypeMirror GetType(DmdType type) => MonoDebugTypeCreator.GetType(this, type);

		sealed class EvalTimedOut { }

		internal DbgDotNetValueResult? CheckFuncEval(DbgEvaluationContext context) {
			debuggerThread.VerifyAccess();
			if (!IsPaused)
				return new DbgDotNetValueResult(PredefinedEvaluationErrorMessages.CanFuncEvalOnlyWhenPaused);
			if (isUnhandledException)
				return new DbgDotNetValueResult(PredefinedEvaluationErrorMessages.CantFuncEvalWhenUnhandledExceptionHasOccurred);
			if (context.ContinueContext.HasData<EvalTimedOut>())
				return new DbgDotNetValueResult(PredefinedEvaluationErrorMessages.FuncEvalTimedOutNowDisabled);
			if (IsEvaluating)
				return new DbgDotNetValueResult(PredefinedEvaluationErrorMessages.CantFuncEval);
			return null;
		}

		void OnFuncEvalComplete(FuncEval funcEval, DbgEvaluationContext context) {
			if (funcEval.EvalTimedOut)
				context.ContinueContext.GetOrCreateData<EvalTimedOut>();
			OnFuncEvalComplete(funcEval);
		}

		void OnFuncEvalComplete(FuncEval funcEval) {
		}

		FuncEval CreateFuncEval(DbgEvaluationContext context, ThreadMirror monoThread, CancellationToken cancellationToken) =>
			funcEvalFactory.CreateFuncEval(a => OnFuncEvalComplete(a, context), monoThread, context.FuncEvalTimeout, suspendOtherThreads: (context.Options & DbgEvaluationContextOptions.RunAllThreads) == 0, cancellationToken: cancellationToken);

		Value TryInvokeMethod(ThreadMirror thread, ObjectMirror obj, MethodMirror method, IList<Value> arguments, out bool timedOut) {
			debuggerThread.VerifyAccess();
			Debug.Assert(!IsEvaluating);
			var funcEvalTimeout = DbgLanguage.DefaultFuncEvalTimeout;
			const bool suspendOtherThreads = true;
			var cancellationToken = CancellationToken.None;
			try {
				timedOut = false;
				using (var funcEval = funcEvalFactory.CreateFuncEval(a => OnFuncEvalComplete(a), thread, funcEvalTimeout, suspendOtherThreads, cancellationToken))
					return funcEval.CallMethod(method, obj, arguments, FuncEvalOptions.None).Result;
			}
			catch (TimeoutException) {
				timedOut = true;
				return null;
			}
		}

		internal DbgDotNetValueResult FuncEvalCall_MonoDebug(DbgEvaluationContext context, DbgThread thread, DmdMethodBase method, DbgDotNetValue obj, object[] arguments, DbgDotNetInvokeOptions invokeOptions, bool newObj, CancellationToken cancellationToken) {
			debuggerThread.VerifyAccess();
			cancellationToken.ThrowIfCancellationRequested();
			var tmp = CheckFuncEval(context);
			if (tmp != null)
				return tmp.Value;
			Debug.Assert(!newObj || method.IsConstructor);

			Debug.Assert(method.SpecialMethodKind == DmdSpecialMethodKind.Metadata, "Methods not defined in metadata should be emulated by other code (i.e., the caller)");
			if (method.SpecialMethodKind != DmdSpecialMethodKind.Metadata)
				return new DbgDotNetValueResult(ErrorHelper.InternalError);

			if (!vm.Version.AtLeast(2, 24) && method is DmdMethodInfo && method.IsConstructedGenericMethod)
				return new DbgDotNetValueResult(dnSpy_Debugger_DotNet_Mono_Resources.Error_RuntimeDoesNotSupportCreatingGenericMethods);

			var funcEvalOptions = FuncEvalOptions.None;
			if ((invokeOptions & DbgDotNetInvokeOptions.NonVirtual) == 0)
				funcEvalOptions |= FuncEvalOptions.Virtual;
			MethodMirror func;
			if ((funcEvalOptions & FuncEvalOptions.Virtual) == 0 || vm.Version.AtLeast(2, 37))
				func = MethodCache.GetMethod(method);
			else {
				func = MethodCache.GetMethod(FindOverloadedMethod(obj?.Type, method));
				funcEvalOptions &= ~FuncEvalOptions.Virtual;
			}

			var monoThread = GetThread(thread);
			try {
				using (var funcEval = CreateFuncEval(context, monoThread, cancellationToken)) {
					var converter = new EvalArgumentConverter(this, funcEval, monoThread.Domain, method.AppDomain);

					var paramTypes = GetAllMethodParameterTypes(method.GetMethodSignature());
					if (paramTypes.Count != arguments.Length)
						throw new InvalidOperationException();

					int argsCount = arguments.Length;
					var args = argsCount == 0 ? Array.Empty<Value>() : new Value[argsCount];
					DmdType origType;
					Value hiddenThisValue;
					if (!method.IsStatic && !newObj) {
						var declType = method.DeclaringType;
						if (method is DmdMethodInfo m)
							declType = m.GetBaseDefinition().DeclaringType;
						var val = converter.Convert(obj, declType, out origType);
						if (val.ErrorMessage != null)
							return new DbgDotNetValueResult(val.ErrorMessage);
						hiddenThisValue = BoxIfNeeded(monoThread.Domain, val.Value, declType, origType);
						if (val.Value == hiddenThisValue && val.Value is StructMirror)
							funcEvalOptions |= FuncEvalOptions.ReturnOutThis;
					}
					else
						hiddenThisValue = null;
					for (int i = 0; i < arguments.Length; i++) {
						var paramType = paramTypes[i];
						var val = converter.Convert(arguments[i], paramType, out origType);
						if (val.ErrorMessage != null)
							return new DbgDotNetValueResult(val.ErrorMessage);
						var valType = origType ?? MonoValueTypeCreator.CreateType(this, val.Value, paramType);
						args[i] = BoxIfNeeded(monoThread.Domain, val.Value, paramType, valType);
					}

					var res = newObj ?
						funcEval.CreateInstance(func, args, funcEvalOptions) :
						funcEval.CallMethod(func, hiddenThisValue, args, funcEvalOptions);
					if (res == null)
						return new DbgDotNetValueResult(ErrorHelper.InternalError);
					var returnType = (method as DmdMethodInfo)?.ReturnType ?? method.ReflectedType;
					var returnValue = res.Exception ?? res.Result ?? new PrimitiveValue(vm, ElementType.Object, null);
					var valueLocation = new NoValueLocation(returnType, returnValue);
					return new DbgDotNetValueResult(CreateDotNetValue_MonoDebug(valueLocation), valueIsException: res.Exception != null);
				}
			}
			catch (TimeoutException) {
				return new DbgDotNetValueResult(PredefinedEvaluationErrorMessages.FuncEvalTimedOut);
			}
			catch (Exception ex) when (ExceptionUtils.IsInternalDebuggerError(ex)) {
				return new DbgDotNetValueResult(ErrorHelper.InternalError);
			}
		}

		Value BoxIfNeeded(AppDomainMirror appDomain, Value value, DmdType targetType, DmdType valueType) {
			if (!targetType.IsValueType && valueType.IsValueType && (value is PrimitiveValue || value is StructMirror))
				return appDomain.CreateBoxedValue(value);
			return value;
		}

		static IList<DmdType> GetAllMethodParameterTypes(DmdMethodSignature sig) {
			if (sig.GetVarArgsParameterTypes().Count == 0)
				return sig.GetParameterTypes();
			var list = new List<DmdType>(sig.GetParameterTypes().Count + sig.GetVarArgsParameterTypes().Count);
			list.AddRange(sig.GetParameterTypes());
			list.AddRange(sig.GetVarArgsParameterTypes());
			return list;
		}

		internal DbgDotNetValueResult Box_MonoDebug(DbgEvaluationContext context, DbgThread thread, Value value, DmdType type, CancellationToken cancellationToken) {
			debuggerThread.VerifyAccess();
			cancellationToken.ThrowIfCancellationRequested();
			var tmp = CheckFuncEval(context);
			if (tmp != null)
				return tmp.Value;

			var monoThread = GetThread(thread);
			try {
				using (var funcEval = CreateFuncEval(context, monoThread, cancellationToken)) {
					value = ValueUtils.MakePrimitiveValueIfPossible(value, type);
					var boxedValue = BoxIfNeeded(monoThread.Domain, value, type.AppDomain.System_Object, type);
					if (boxedValue == null)
						return new DbgDotNetValueResult(ErrorHelper.InternalError);
					return new DbgDotNetValueResult(CreateDotNetValue_MonoDebug(type.AppDomain, boxedValue), valueIsException: false);
				}
			}
			catch (TimeoutException) {
				return new DbgDotNetValueResult(PredefinedEvaluationErrorMessages.FuncEvalTimedOut);
			}
			catch (Exception ex) when (ExceptionUtils.IsInternalDebuggerError(ex)) {
				return new DbgDotNetValueResult(ErrorHelper.InternalError);
			}
		}

		internal DbgDotNetCreateValueResult CreateValue_MonoDebug(DbgEvaluationContext context, DbgThread thread, object value, CancellationToken cancellationToken) {
			debuggerThread.VerifyAccess();
			cancellationToken.ThrowIfCancellationRequested();
			if (value is DbgDotNetValueImpl)
				return new DbgDotNetCreateValueResult((DbgDotNetValueImpl)value);
			var tmp = CheckFuncEval(context);
			if (tmp != null)
				return new DbgDotNetCreateValueResult(tmp.Value.ErrorMessage ?? throw new InvalidOperationException());

			var monoThread = GetThread(thread);
			try {
				var reflectionAppDomain = thread.AppDomain.GetReflectionAppDomain();
				using (var funcEval = CreateFuncEval(context, monoThread, cancellationToken)) {
					var converter = new EvalArgumentConverter(this, funcEval, monoThread.Domain, reflectionAppDomain);
					var evalRes = converter.Convert(value, reflectionAppDomain.System_Object, out var newValueType);
					if (evalRes.ErrorMessage != null)
						return new DbgDotNetCreateValueResult(evalRes.ErrorMessage);

					var resultValue = CreateDotNetValue_MonoDebug(reflectionAppDomain, evalRes.Value);
					return new DbgDotNetCreateValueResult(resultValue);
				}
			}
			catch (TimeoutException) {
				return new DbgDotNetCreateValueResult(PredefinedEvaluationErrorMessages.FuncEvalTimedOut);
			}
			catch (Exception ex) when (ExceptionUtils.IsInternalDebuggerError(ex)) {
				return new DbgDotNetCreateValueResult(ErrorHelper.InternalError);
			}
		}

		internal DbgCreateMonoValueResult CreateMonoValue_MonoDebug(DbgEvaluationContext context, DbgThread thread, object value, DmdType targetType, CancellationToken cancellationToken) {
			debuggerThread.VerifyAccess();
			cancellationToken.ThrowIfCancellationRequested();
			if (value is DbgDotNetValueImpl)
				return new DbgCreateMonoValueResult(((DbgDotNetValueImpl)value).Value);
			var tmp = CheckFuncEval(context);
			if (tmp != null)
				return new DbgCreateMonoValueResult(tmp.Value.ErrorMessage ?? throw new InvalidOperationException());

			var monoThread = GetThread(thread);
			try {
				var reflectionAppDomain = thread.AppDomain.GetReflectionAppDomain();
				using (var funcEval = CreateFuncEval(context, monoThread, cancellationToken)) {
					var converter = new EvalArgumentConverter(this, funcEval, monoThread.Domain, reflectionAppDomain);
					var evalRes = converter.Convert(value, reflectionAppDomain.System_Object, out var newValueType);
					if (evalRes.ErrorMessage != null)
						return new DbgCreateMonoValueResult(evalRes.ErrorMessage);
					var newValue = evalRes.Value;
					if (targetType.IsEnum)
						newValue = MonoVirtualMachine.CreateEnumMirror(GetType(targetType), (PrimitiveValue)newValue);
					newValue = BoxIfNeeded(monoThread.Domain, newValue, targetType, newValueType);
					return new DbgCreateMonoValueResult(newValue);
				}
			}
			catch (TimeoutException) {
				return new DbgCreateMonoValueResult(PredefinedEvaluationErrorMessages.FuncEvalTimedOut);
			}
			catch (Exception ex) when (ExceptionUtils.IsInternalDebuggerError(ex)) {
				return new DbgCreateMonoValueResult(ErrorHelper.InternalError);
			}
		}

		static DmdMethodBase FindOverloadedMethod(DmdType objType, DmdMethodBase method) {
			if ((object)objType == null)
				return method;
			if (!method.IsVirtual && !method.IsAbstract)
				return method;
			if (method.DeclaringType == objType)
				return method;
			var comparer = new DmdSigComparer(DmdMemberInfoEqualityComparer.DefaultMember.Options & ~DmdSigComparerOptions.CompareDeclaringType);
			foreach (var m in objType.Methods) {
				if (!m.IsVirtual)
					continue;
				if (comparer.Equals(m, method))
					return m;
			}
			if (method.DeclaringType.IsInterface) {
				var sig = method.GetMethodSignature();
				foreach (var m in objType.Methods) {
					if (!m.IsVirtual)
						continue;

					//TODO: Use MethodImpl table. For now assume it's an explicitly implemented iface method if it's private and name doesn't match original name
					if (!m.IsPrivate || m.Name == method.Name)
						continue;

					if (comparer.Equals(m.GetMethodSignature(), sig))
						return m;
				}
			}
			return method;
		}
	}

	struct DbgCreateMonoValueResult {
		public Value Value { get; }
		public string ErrorMessage { get; }
		public DbgCreateMonoValueResult(Value value) {
			Value = value ?? throw new ArgumentNullException(nameof(value));
			ErrorMessage = null;
		}
		public DbgCreateMonoValueResult(string errorMessage) {
			Value = null;
			ErrorMessage = errorMessage ?? throw new ArgumentNullException(nameof(errorMessage));
		}
	}
}
