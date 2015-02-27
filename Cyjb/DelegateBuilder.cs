﻿using System;
using System.Diagnostics.Contracts;
using System.Linq.Expressions;
using System.Reflection;
using System.Reflection.Emit;
using Cyjb.Reflection;

namespace Cyjb
{
	/// <summary>
	/// 表示通用方法的调用委托。
	/// </summary>
	/// <param name="instance">要调用方法的实例。</param>
	/// <param name="parameters">方法的参数。</param>
	/// <returns>方法的返回值。</returns>
	public delegate object MethodInvoker(object instance, params object[] parameters);
	/// <summary>
	/// 表示通用构造函数的调用委托。
	/// </summary>
	/// <param name="parameters">构造函数的参数。</param>
	/// <returns>新创建的实例。</returns>
	public delegate object InstanceCreator(params object[] parameters);
	/// <summary>
	/// 提供动态构造方法、属性或字段委托的方法。
	/// </summary>
	/// <example>
	/// 一下是一些简单的示例，很容易构造出需要的委托。
	/// <code>
	/// class Program {
	/// 	public delegate void MyDelegate(params int[] args);
	/// 	public static void TestMethod(int value) { }
	/// 	public void TestMethod(uint value) { }
	/// 	public static void TestMethod{T}(params T[] arg) { }
	/// 	static void Main(string[] args) {
	/// 		Type type = typeof(Program);
	/// 		Action&lt;int&gt; m1 = type.CreateDelegate&lt;Action&lt;int&gt;&gt;("TestMethod");
	/// 		m1(10);
	/// 		Program p = new Program();
	/// 		Action&lt;Program, uint&gt; m2 = type.CreateDelegate&lt;Action&lt;Program, uint&gt;&gt;("TestMethod");
	/// 		m2(p, 10);
	/// 		Action&lt;object, uint} m3 = type.CreateDelegate&lt;Action&lt;object, uint&gt;&gt;("TestMethod");
	/// 		m3(p, 10);
	/// 		Action&lt;uint} m4 = type.CreateDelegate&lt;Action&lt;uint&gt;&gt;("TestMethod", p);
	/// 		m4(10);
	/// 		MyDelegate m5 = type.CreateDelegate&lt;MyDelegate&gt;("TestMethod");
	/// 		m5(0, 1, 2);
	/// 	}
	/// }
	/// </code>
	/// </example>
	/// <remarks>
	/// <para><see cref="DelegateBuilder"/> 类提供的 <c>CreateDelegate</c> 方法，其的用法与 
	/// <c>Delegate.CreateDelegate</c> 完全相同，功能却大大丰富，
	/// 几乎可以只依靠委托类型、反射类型和成员名称构造出任何需要的委托，
	/// 省去了自己反射获取类型成员的过程。</para>
	/// <para>关于对反射创建委托的效率问题，以及该类的实现原理，可以参见我的博文 
	/// <see href="http://www.cnblogs.com/cyjb/archive/p/DelegateBuilder.html">
	/// 《C# 反射的委托创建器》</see>。</para>
	/// </remarks>
	/// <seealso href="http://www.cnblogs.com/cyjb/archive/p/DelegateBuilder.html">《C# 反射的委托创建器》</seealso>
	public static partial class DelegateBuilder
	{

		#region 通用委托

		/// <summary>
		/// 创建表示指定的静态或实例方法的的委托。如果是实例方法，需要将实例对象作为第一个参数；
		/// 如果是静态方法，则第一个参数无效。对于可变参数方法，只支持固定参数。
		/// </summary>
		/// <param name="method">描述委托要表示的静态或实例方法的 <see cref="MethodInfo"/>。</param>
		/// <returns>表示指定的静态或实例方法的委托。</returns>
		/// <exception cref="ArgumentNullException"><paramref name="method"/> 为 <c>null</c>。</exception>
		/// <exception cref="ArgumentException"><paramref name="method"/> 是开放构造方法。</exception>
		/// <exception cref="MethodAccessException">调用方无权访问 <paramref name="method"/>。</exception>
		public static MethodInvoker CreateDelegate(this MethodInfo method)
		{
			if (method == null)
			{
				throw CommonExceptions.ArgumentNull("method");
			}
			Contract.Ensures(Contract.Result<MethodInvoker>() != null);
			if (method.ContainsGenericParameters)
			{
				// 不能对开放构造方法执行绑定。
				throw CommonExceptions.BindOpenConstructedMethod("method");
			}
			DynamicMethod dlgMethod = new DynamicMethod("MethodInvoker", typeof(object),
				new[] { typeof(object), typeof(object[]) }, method.Module, true);
			ILGenerator il = dlgMethod.GetILGenerator();
			Contract.Assume(il != null);
			ParameterInfo[] parameters = method.GetParametersNoCopy();
			int len = parameters.Length;
			// 参数数量检测。
			if (len > 0)
			{
				il.EmitCheckArgumentNull(1, "parameters");
				il.Emit(OpCodes.Ldarg_1);
				il.EmitCheckTargetParameterCount(parameters.Length);
			}
			// 加载实例对象。
			if (!method.IsStatic)
			{
				EmitLoadInstance(il, method, typeof(object));
			}
			bool optimizeTailcall = true;
			// 加载方法参数。
			for (int i = 0; i < len; i++)
			{
				il.Emit(OpCodes.Ldarg_1);
				il.EmitInt(i);
				Type paramType = parameters[i].ParameterType;
				if (paramType.IsByRef)
				{
					paramType = paramType.GetElementType();
					Converter converter = il.GetConversion(typeof(object), paramType, ConversionType.Explicit);
					Console.WriteLine(converter);
					if (converter.NeedEmit)
					{
						il.Emit(OpCodes.Ldelem_Ref);
						converter.Emit(true);
						LocalBuilder local = il.DeclareLocal(paramType);
						il.Emit(OpCodes.Stloc, local);
						il.Emit(OpCodes.Ldloca, local);
						optimizeTailcall = false;
					}
					else
					{
						il.Emit(OpCodes.Ldelema, paramType);
					}
				}
				else
				{
					il.Emit(OpCodes.Ldelem_Ref);
					il.EmitConversion(typeof(object), paramType, true, ConversionType.Explicit);
				}
			}
			// 调用函数。
			EmitInvokeMethod(il, method, null, typeof(object), optimizeTailcall);
			return (MethodInvoker)dlgMethod.CreateDelegate(typeof(MethodInvoker));
		}
		/// <summary>
		/// 创建表示指定的构造函数的的委托。对于可变参数方法，只支持固定参数。
		/// </summary>
		/// <param name="ctor">描述委托要表示的构造函数的 <see cref="ConstructorInfo"/>。</param>
		/// <returns>表示指定的构造函数的委托。</returns>
		/// <exception cref="ArgumentNullException"><paramref name="ctor"/> 为 <c>null</c>。</exception>
		/// <exception cref="MethodAccessException">调用方无权访问 <paramref name="ctor"/>。</exception>
		public static InstanceCreator CreateDelegate(this ConstructorInfo ctor)
		{
			if (ctor == null)
			{
				throw CommonExceptions.ArgumentNull("type");
			}
			Contract.EndContractBlock();
			DynamicMethod dlgMethod = new DynamicMethod("InstanceCreator", typeof(object),
				new[] { typeof(object[]) }, ctor.Module, true);
			ILGenerator il = dlgMethod.GetILGenerator();
			Contract.Assume(il != null);
			ParameterInfo[] parameters = ctor.GetParametersNoCopy();
			int len = parameters.Length;
			// 参数数量检测。
			if (len > 0)
			{
				il.EmitCheckArgumentNull(0, "parameters");
				il.Emit(OpCodes.Ldarg_0);
				il.EmitCheckTargetParameterCount(parameters.Length);
			}
			// 加载方法参数。
			for (int i = 0; i < len; i++)
			{
				il.Emit(OpCodes.Ldarg_0);
				il.EmitInt(i);
				il.Emit(OpCodes.Ldelem_Ref);
				il.EmitConversion(typeof(object), parameters[i].ParameterType, true, ConversionType.Explicit);
			}
			// 对实例进行类型转换。
			Converter converter = il.GetConversion(ctor.DeclaringType, typeof(object), ConversionType.Explicit);
			il.Emit(OpCodes.Newobj, ctor);
			converter.Emit(true);
			il.Emit(OpCodes.Ret);
			return (InstanceCreator)dlgMethod.CreateDelegate(typeof(InstanceCreator));
		}
		/// <summary>
		/// 创建表示指定类型的默认构造函数的的委托。对于可变参数方法，只支持固定参数。
		/// </summary>
		/// <param name="type">描述委托要创建的实例的类型。</param>
		/// <returns>表示指定类型的默认构造函数的委托。</returns>
		/// <exception cref="ArgumentNullException"><paramref name="type"/> 为 <c>null</c>。</exception>
		/// <exception cref="ArgumentException"><paramref name="type"/> 包含泛型参数。</exception>
		public static InstanceCreator CreateInstanceCreator(this Type type)
		{
			if (type == null)
			{
				throw CommonExceptions.ArgumentNull("type");
			}
			Contract.EndContractBlock();
			if (type.ContainsGenericParameters)
			{
				throw CommonExceptions.TypeContainsGenericParameters(type);
			}
			DynamicMethod dlgMethod = new DynamicMethod("InstanceCreator", typeof(object),
				new[] { typeof(object[]) }, type.Module, true);
			ILGenerator il = dlgMethod.GetILGenerator();
			Contract.Assume(il != null);
			// 对实例进行类型转换。
			Converter converter = il.GetConversion(type, typeof(object), ConversionType.Explicit);
			il.EmitNew(type);
			converter.Emit(true);
			il.Emit(OpCodes.Ret);
			return (InstanceCreator)dlgMethod.CreateDelegate(typeof(InstanceCreator));
		}

		#endregion // 通用委托


		#region 从 PropertyInfo 构造属性委托

		/// <summary>
		/// 创建用于表示获取或设置指定静态或实例属性的指定类型的委托。
		/// 如果是实例属性，需要将实例对象作为委托的第一个参数。
		/// 如果委托具有返回值，则认为是获取属性，否则认为是设置属性。
		/// 支持参数的强制类型转换，参数声明可以与实际类型不同。
		/// </summary>
		/// <typeparam name="TDelegate">要创建的委托的类型。</typeparam>
		/// <param name="property">描述委托要表示的静态或实例属性的 
		/// <see cref="System.Reflection.PropertyInfo"/>。</param>
		/// <returns>指定类型的委托，表示获取或设置指定的静态或实例属性。</returns>
		/// <exception cref="System.ArgumentNullException"><paramref name="property"/> 为 <c>null</c>。</exception>
		/// <exception cref="System.ArgumentException"><typeparamref name="TDelegate"/> 不继承
		/// <see cref="System.MulticastDelegate"/>。</exception>
		/// <exception cref="System.ArgumentException">不能绑定 <paramref name="property"/>。</exception>
		/// <exception cref="System.MissingMethodException">未找到 <typeparamref name="TDelegate"/>
		/// 的 <c>Invoke</c> 方法。</exception>
		/// <exception cref="System.MethodAccessException">调用方无权访问
		/// <paramref name="property"/>。</exception>
		public static TDelegate CreateDelegate<TDelegate>(this PropertyInfo property)
			where TDelegate : class
		{
			return CreateDelegate(typeof(TDelegate), property, true) as TDelegate;
		}
		/// <summary>
		/// 使用针对绑定失败的指定行为，创建用于表示获取或设置指定静态或实例属性的指定类型的委托。
		/// 如果是实例属性，需要将实例对象作为委托的第一个参数。
		/// 如果委托具有返回值，则认为是获取属性，否则认为是设置属性。
		/// 支持参数的强制类型转换，参数声明可以与实际类型不同。
		/// </summary>
		/// <typeparam name="TDelegate">要创建的委托的类型。</typeparam>
		/// <param name="property">描述委托要表示的静态或实例属性的 
		/// <see cref="System.Reflection.PropertyInfo"/>。</param>
		/// <param name="throwOnBindFailure">为 <c>true</c>，表示无法绑定 <paramref name="property"/> 
		/// 时引发异常；否则为 <c>false</c>。</param>
		/// <returns>指定类型的委托，表示获取或设置指定的静态或实例属性。</returns>
		/// <exception cref="System.ArgumentNullException"><paramref name="property"/> 为 <c>null</c>。</exception>
		/// <exception cref="System.ArgumentException"><typeparamref name="TDelegate"/> 不继承
		/// <see cref="System.MulticastDelegate"/>。</exception>
		/// <exception cref="System.ArgumentException">不能绑定 <paramref name="property"/> 
		/// 且 <paramref name="throwOnBindFailure"/> 为 <c>true</c>。</exception>
		/// <exception cref="System.MissingMethodException">未找到 <typeparamref name="TDelegate"/>
		/// 的 <c>Invoke</c> 方法。</exception>
		/// <exception cref="System.MethodAccessException">调用方无权访问
		/// <paramref name="property"/>。</exception>
		public static TDelegate CreateDelegate<TDelegate>(this PropertyInfo property, bool throwOnBindFailure)
			where TDelegate : class
		{
			return CreateDelegate(typeof(TDelegate), property, throwOnBindFailure) as TDelegate;
		}
		/// <summary>
		/// 创建用于表示获取或设置指定静态或实例属性的指定类型的委托。
		/// 如果是实例属性，需要将实例对象作为委托的第一个参数。
		/// 如果委托具有返回值，则认为是获取属性，否则认为是设置属性。
		/// 支持参数的强制类型转换，参数声明可以与实际类型不同。
		/// </summary>
		/// <param name="type">要创建的委托的类型。</param>
		/// <param name="property">描述委托要表示的静态或实例属性的 
		/// <see cref="System.Reflection.PropertyInfo"/>。</param>
		/// <returns>指定类型的委托，表示获取或设置指定的静态或实例属性。</returns>
		/// <exception cref="System.ArgumentNullException"><paramref name="type"/> 为 <c>null</c>。</exception>
		/// <exception cref="System.ArgumentNullException"><paramref name="property"/> 为 <c>null</c>。</exception>
		/// <exception cref="System.ArgumentException"><paramref name="type"/> 不继承
		/// <see cref="System.MulticastDelegate"/>。</exception>
		/// <exception cref="System.ArgumentException">不能绑定 <paramref name="property"/>。</exception>
		/// <exception cref="System.MissingMethodException">未找到 <paramref name="type"/>
		/// 的 <c>Invoke</c> 方法。</exception>
		/// <exception cref="System.MethodAccessException">调用方无权访问
		/// <paramref name="property"/>。</exception>
		public static Delegate CreateDelegate(Type type, PropertyInfo property)
		{
			return CreateDelegate(type, property, true);
		}
		/// <summary>
		/// 使用针对绑定失败的指定行为，创建用于表示获取或设置指定静态或实例属性的指定类型的委托。
		/// 如果是实例属性，需要将实例对象作为委托的第一个参数。
		/// 如果委托具有返回值，则认为是获取属性，否则认为是设置属性。
		/// 支持参数的强制类型转换，参数声明可以与实际类型不同。
		/// </summary>
		/// <param name="type">要创建的委托的类型。</param>
		/// <param name="property">描述委托要表示的静态或实例属性的 
		/// <see cref="System.Reflection.PropertyInfo"/>。</param>
		/// <param name="throwOnBindFailure">为 <c>true</c>，表示无法绑定 <paramref name="property"/> 
		/// 时引发异常；否则为 <c>false</c>。</param>
		/// <returns>指定类型的委托，表示获取或设置指定的静态或实例属性。</returns>
		/// <exception cref="System.ArgumentNullException"><paramref name="type"/> 为 <c>null</c>。</exception>
		/// <exception cref="System.ArgumentNullException"><paramref name="property"/> 为 <c>null</c>。</exception>
		/// <exception cref="System.ArgumentException"><paramref name="type"/> 不继承 
		/// <see cref="System.MulticastDelegate"/>。</exception>
		/// <exception cref="System.ArgumentException">不能绑定 <paramref name="property"/> 
		/// 且 <paramref name="throwOnBindFailure"/> 为 <c>true</c>。</exception>
		/// <exception cref="System.MissingMethodException">未找到 <paramref name="type"/> 
		/// 的 <c>Invoke</c> 方法。</exception>
		/// <exception cref="System.MethodAccessException">调用方无权访问 
		/// <paramref name="property"/>。</exception>
		public static Delegate CreateDelegate(Type type, PropertyInfo property, bool throwOnBindFailure)
		{
			CommonExceptions.CheckArgumentNull(property, "property");
			CommonExceptions.CheckDelegateType(type, "type");
			MethodInfo invoke = type.GetMethod("Invoke");
			// 判断是获取属性还是设置属性。
			MethodInfo method = null;
			if (invoke.ReturnType == typeof(void))
			{
				method = property.GetSetMethod(true);
				if (method == null && throwOnBindFailure)
				{
					throw CommonExceptions.BindTargetPropertyNoSet("property");
				}
			}
			else
			{
				method = property.GetGetMethod(true);
				if (method == null && throwOnBindFailure)
				{
					throw CommonExceptions.BindTargetPropertyNoGet("property");
				}
			}
			Delegate dlg = null;
			if (method != null)
			{
				// 创建委托。
				dlg = CreateOpenDelegate(type, invoke, method);
			}
			if (dlg == null && throwOnBindFailure)
			{
				throw CommonExceptions.BindTargetProperty("property");
			}
			return dlg;
		}

		#endregion // 从 PropertyInfo 构造属性委托

		#region 从 PropertyInfo 构造带有第一个参数的属性委托

		/// <summary>
		/// 使用指定的第一个参数，创建表示获取或设置指定的静态或实例属性的指定类型的委托。 
		/// 如果委托具有返回值，则认为是获取属性，否则认为是设置属性。
		/// 支持参数的强制类型转换，参数声明可以与实际类型不同。
		/// </summary>
		/// <typeparam name="TDelegate">要创建的委托的类型。</typeparam>
		/// <param name="property">描述委托要表示的静态或实例属性的 
		/// <see cref="System.Reflection.PropertyInfo"/>。</param>
		/// <param name="firstArgument">委托要绑定到的对象，或为 <c>null</c>，
		/// 后者表示将 <paramref name="property"/> 视为 <c>static</c>。</param>
		/// <returns>指定类型的委托，表示获取或设置指定的静态或实例属性。</returns>
		/// <exception cref="System.ArgumentNullException"><paramref name="property"/> 为 <c>null</c>。</exception>
		/// <exception cref="System.ArgumentException"><typeparamref name="TDelegate"/> 不继承
		/// <see cref="System.MulticastDelegate"/>。</exception>
		/// <exception cref="System.ArgumentException">不能绑定 <paramref name="property"/>。</exception>
		/// <exception cref="System.MissingMethodException">未找到 <typeparamref name="TDelegate"/>
		/// 的 <c>Invoke</c> 方法。</exception>
		/// <exception cref="System.MethodAccessException">调用方无权访问
		/// <paramref name="property"/>。</exception>
		public static TDelegate CreateDelegate<TDelegate>(this PropertyInfo property, object firstArgument)
			where TDelegate : class
		{
			return CreateDelegate(typeof(TDelegate), property, firstArgument, true) as TDelegate;
		}
		/// <summary>
		/// 使用指定的第一个参数和针对绑定失败的指定行为，创建表示获取或设置指定的静态或实例属性的指定类型的委托。 
		/// 如果委托具有返回值，则认为是获取属性，否则认为是设置属性。
		/// 支持参数的强制类型转换，参数声明可以与实际类型不同。
		/// </summary>
		/// <typeparam name="TDelegate">要创建的委托的类型。</typeparam>
		/// <param name="property">描述委托要表示的静态或实例属性的 
		/// <see cref="System.Reflection.PropertyInfo"/>。</param>
		/// <param name="firstArgument">委托要绑定到的对象，或为 <c>null</c>，
		/// 后者表示将 <paramref name="property"/> 视为 <c>static</c>。</param>
		/// <param name="throwOnBindFailure">为 <c>true</c>，表示无法绑定 <paramref name="property"/> 
		/// 时引发异常；否则为 <c>false</c>。</param>
		/// <returns>指定类型的委托，表示获取或设置指定的静态或实例属性。</returns>
		/// <exception cref="System.ArgumentNullException"><paramref name="property"/> 为 <c>null</c>。</exception>
		/// <exception cref="System.ArgumentException"><typeparamref name="TDelegate"/> 不继承
		/// <see cref="System.MulticastDelegate"/>。</exception>
		/// <exception cref="System.ArgumentException">不能绑定 <paramref name="property"/> 
		/// 且 <paramref name="throwOnBindFailure"/> 为 <c>true</c>。</exception>
		/// <exception cref="System.MissingMethodException">未找到 <typeparamref name="TDelegate"/>
		/// 的 <c>Invoke</c> 方法。</exception>
		/// <exception cref="System.MethodAccessException">调用方无权访问
		/// <paramref name="property"/>。</exception>
		public static TDelegate CreateDelegate<TDelegate>(this PropertyInfo property,
			object firstArgument, bool throwOnBindFailure)
			where TDelegate : class
		{
			return CreateDelegate(typeof(TDelegate), property, firstArgument, throwOnBindFailure) as TDelegate;
		}
		/// <summary>
		/// 使用指定的第一个参数，创建表示获取或设置指定的静态或实例属性的指定类型的委托。 
		/// 如果委托具有返回值，则认为是获取属性，否则认为是设置属性。
		/// 支持参数的强制类型转换，参数声明可以与实际类型不同。
		/// </summary>
		/// <param name="type">要创建的委托的类型。</param>
		/// <param name="property">描述委托要表示的静态或实例属性的 
		/// <see cref="System.Reflection.PropertyInfo"/>。</param>
		/// <param name="firstArgument">委托要绑定到的对象，或为 <c>null</c>，
		/// 后者表示将 <paramref name="property"/> 视为 <c>static</c>。</param>
		/// <returns>指定类型的委托，表示获取或设置指定的静态或实例属性。</returns>
		/// <exception cref="System.ArgumentNullException"><paramref name="type"/> 为 <c>null</c>。</exception>
		/// <exception cref="System.ArgumentNullException"><paramref name="property"/> 为 <c>null</c>。</exception>
		/// <exception cref="System.ArgumentException"><paramref name="type"/> 不继承
		/// <see cref="System.MulticastDelegate"/>。</exception>
		/// <exception cref="System.ArgumentException">不能绑定 <paramref name="property"/>。</exception>
		/// <exception cref="System.MissingMethodException">未找到 <paramref name="type"/>
		/// 的 <c>Invoke</c> 方法。</exception>
		/// <exception cref="System.MethodAccessException">调用方无权访问
		/// <paramref name="property"/>。</exception>
		public static Delegate CreateDelegate(Type type, PropertyInfo property, object firstArgument)
		{
			return CreateDelegate(type, property, firstArgument, true);
		}
		/// <summary>
		/// 使用指定的第一个参数和针对绑定失败的指定行为，创建表示获取或设置指定的静态或实例属性的指定类型的委托。 
		/// 如果委托具有返回值，则认为是获取属性，否则认为是设置属性。
		/// 支持参数的强制类型转换，参数声明可以与实际类型不同。
		/// </summary>
		/// <param name="type">要创建的委托的类型。</param>
		/// <param name="property">描述委托要表示的静态或实例属性的 
		/// <see cref="System.Reflection.PropertyInfo"/>。</param>
		/// <param name="firstArgument">委托要绑定到的对象，或为 <c>null</c>，
		/// 后者表示将 <paramref name="property"/> 视为 <c>static</c>。</param>
		/// <param name="throwOnBindFailure">为 <c>true</c>，表示无法绑定 <paramref name="property"/> 
		/// 时引发异常；否则为 <c>false</c>。</param>
		/// <returns>指定类型的委托，表示获取或设置指定的静态或实例属性。</returns>
		/// <exception cref="System.ArgumentNullException"><paramref name="type"/> 为 <c>null</c>。</exception>
		/// <exception cref="System.ArgumentNullException"><paramref name="property"/> 为 <c>null</c>。</exception>
		/// <exception cref="System.ArgumentException"><paramref name="type"/> 不继承
		/// <see cref="System.MulticastDelegate"/>。</exception>
		/// <exception cref="System.ArgumentException">不能绑定 <paramref name="property"/> 
		/// 且 <paramref name="throwOnBindFailure"/> 为 <c>true</c>。</exception>
		/// <exception cref="System.MissingMethodException">未找到 <paramref name="type"/>
		/// 的 <c>Invoke</c> 方法。</exception>
		/// <exception cref="System.MethodAccessException">调用方无权访问
		/// <paramref name="property"/>。</exception>
		public static Delegate CreateDelegate(Type type, PropertyInfo property, object firstArgument,
			bool throwOnBindFailure)
		{
			CommonExceptions.CheckArgumentNull(property, "property");
			CommonExceptions.CheckDelegateType(type, "type");
			MethodInfo invoke = type.GetMethod("Invoke");
			// 判断是获取属性还是设置属性。
			MethodInfo method = null;
			if (invoke.ReturnType == typeof(void))
			{
				method = property.GetSetMethod(true);
				if (method == null && throwOnBindFailure)
				{
					throw CommonExceptions.BindTargetPropertyNoSet("property");
				}
			}
			else
			{
				method = property.GetGetMethod(true);
				if (method == null && throwOnBindFailure)
				{
					throw CommonExceptions.BindTargetPropertyNoGet("property");
				}
			}
			Delegate dlg = null;
			if (method != null)
			{
				// 创建委托。
				dlg = CreateDelegateWithArgument(type, firstArgument,
					invoke, invoke.GetParameters(), method, method.GetParameters());
			}
			if (dlg == null && throwOnBindFailure)
			{
				throw CommonExceptions.BindTargetProperty("property");
			}
			return dlg;
		}

		#endregion // 从 PropertyInfo 构造带有第一个参数的属性委托

		#region 从 FieldInfo 构造字段委托

		/// <summary>
		/// 创建表示获取或设置指定的静态或实例字段的指定类型的委托。 
		/// 如果委托具有返回值，则认为是获取字段，否则认为是设置字段。
		/// 支持参数的强制类型转换，参数声明可以与实际类型不同。
		/// </summary>
		/// <typeparam name="TDelegate">要创建的委托的类型。</typeparam>
		/// <param name="field">描述委托要表示的静态或实例字段的 
		/// <see cref="System.Reflection.PropertyInfo"/>。</param>
		/// <returns>指定类型的委托，表示获取或设置指定的静态或实例字段。</returns>
		/// <exception cref="System.ArgumentNullException"><paramref name="field"/> 为 <c>null</c>。</exception>
		/// <exception cref="System.ArgumentException"><typeparamref name="TDelegate"/> 不继承
		/// <see cref="System.MulticastDelegate"/>。</exception>
		/// <exception cref="System.ArgumentException">不能绑定 <paramref name="field"/>。</exception>
		/// <exception cref="System.MissingMethodException">未找到 <typeparamref name="TDelegate"/>
		/// 的 <c>Invoke</c> 方法。</exception>
		/// <exception cref="System.MethodAccessException">调用方无权访问
		/// <paramref name="field"/>。</exception>
		public static TDelegate CreateDelegate<TDelegate>(this FieldInfo field)
			where TDelegate : class
		{
			return CreateDelegate(typeof(TDelegate), field, true) as TDelegate;
		}
		/// <summary>
		/// 使用针对绑定失败的指定行为，创建表示获取或设置指定的静态或实例字段的指定类型的委托。 
		/// 如果委托具有返回值，则认为是获取字段，否则认为是设置字段。
		/// 支持参数的强制类型转换，参数声明可以与实际类型不同。
		/// </summary>
		/// <typeparam name="TDelegate">要创建的委托的类型。</typeparam>
		/// <param name="field">描述委托要表示的静态或实例字段的 
		/// <see cref="System.Reflection.PropertyInfo"/>。</param>
		/// <param name="throwOnBindFailure">为 <c>true</c>，表示无法绑定 <paramref name="field"/> 
		/// 时引发异常；否则为 <c>false</c>。</param>
		/// <returns>指定类型的委托，表示获取或设置指定的静态或实例字段。</returns>
		/// <exception cref="System.ArgumentNullException"><paramref name="field"/> 为 <c>null</c>。</exception>
		/// <exception cref="System.ArgumentException"><typeparamref name="TDelegate"/> 不继承
		/// <see cref="System.MulticastDelegate"/>。</exception>
		/// <exception cref="System.ArgumentException">不能绑定 <paramref name="field"/> 
		/// 且 <paramref name="throwOnBindFailure"/> 为 <c>true</c>。</exception>
		/// <exception cref="System.MissingMethodException">未找到 <typeparamref name="TDelegate"/>
		/// 的 <c>Invoke</c> 方法。</exception>
		/// <exception cref="System.MethodAccessException">调用方无权访问
		/// <paramref name="field"/>。</exception>
		public static TDelegate CreateDelegate<TDelegate>(this FieldInfo field, bool throwOnBindFailure)
			where TDelegate : class
		{
			return CreateDelegate(typeof(TDelegate), field, throwOnBindFailure) as TDelegate;
		}
		/// <summary>
		/// 创建表示获取或设置指定的静态或实例字段的指定类型的委托。 
		/// 如果委托具有返回值，则认为是获取字段，否则认为是设置字段。
		/// 支持参数的强制类型转换，参数声明可以与实际类型不同。
		/// </summary>
		/// <param name="type">要创建的委托的类型。</param>
		/// <param name="field">描述委托要表示的静态或实例字段的 
		/// <see cref="System.Reflection.PropertyInfo"/>。</param>
		/// <returns>指定类型的委托，表示获取或设置指定的静态或实例字段。</returns>
		/// <exception cref="System.ArgumentNullException"><paramref name="type"/> 为 <c>null</c>。</exception>
		/// <exception cref="System.ArgumentNullException"><paramref name="field"/> 为 <c>null</c>。</exception>
		/// <exception cref="System.ArgumentException"><paramref name="type"/> 不继承
		/// <see cref="System.MulticastDelegate"/>。</exception>
		/// <exception cref="System.ArgumentException">不能绑定 <paramref name="field"/>。</exception>
		/// <exception cref="System.MissingMethodException">未找到 <paramref name="type"/>
		/// 的 <c>Invoke</c> 方法。</exception>
		/// <exception cref="System.MethodAccessException">调用方无权访问
		/// <paramref name="field"/>。</exception>
		public static Delegate CreateDelegate(Type type, FieldInfo field)
		{
			return CreateDelegate(type, field, true);
		}
		/// <summary>
		/// 使用针对绑定失败的指定行为，创建表示获取或设置指定的静态或实例字段的指定类型的委托。 
		/// 如果委托具有返回值，则认为是获取字段，否则认为是设置字段。
		/// 支持参数的强制类型转换，参数声明可以与实际类型不同。
		/// </summary>
		/// <param name="type">要创建的委托的类型。</param>
		/// <param name="field">描述委托要表示的静态或实例字段的 
		/// <see cref="System.Reflection.PropertyInfo"/>。</param>
		/// <param name="throwOnBindFailure">为 <c>true</c>，表示无法绑定 <paramref name="field"/> 
		/// 时引发异常；否则为 <c>false</c>。</param>
		/// <returns>指定类型的委托，表示获取或设置指定的静态或实例字段。</returns>
		/// <exception cref="System.ArgumentNullException"><paramref name="type"/> 为 <c>null</c>。</exception>
		/// <exception cref="System.ArgumentNullException"><paramref name="field"/> 为 <c>null</c>。</exception>
		/// <exception cref="System.ArgumentException"><paramref name="type"/> 不继承
		/// <see cref="System.MulticastDelegate"/>。</exception>
		/// <exception cref="System.ArgumentException">不能绑定 <paramref name="field"/> 
		/// 且 <paramref name="throwOnBindFailure"/> 为 <c>true</c>。</exception>
		/// <exception cref="System.MissingMethodException">未找到 <paramref name="type"/>
		/// 的 <c>Invoke</c> 方法。</exception>
		/// <exception cref="System.MethodAccessException">调用方无权访问
		/// <paramref name="field"/>。</exception>
		public static Delegate CreateDelegate(Type type, FieldInfo field, bool throwOnBindFailure)
		{
			CommonExceptions.CheckArgumentNull(field, "field");
			CommonExceptions.CheckDelegateType(type, "type");
			MethodInfo invoke = type.GetMethod("Invoke");
			Delegate dlg = CreateOpenDelegate(type, field, invoke, invoke.GetParameters());
			if (dlg == null && throwOnBindFailure)
			{
				throw CommonExceptions.BindTargetField("field");
			}
			return dlg;
		}
		/// <summary>
		/// 创建指定的静态或实例字段的指定类型的开放字段委托。
		/// 如果是实例字段，需要将实例对象作为委托的第一个参数。
		/// 支持参数的强制类型转换，参数声明可以与实际类型不同。
		/// </summary>
		/// <param name="type">要创建的委托的类型。</param>
		/// <param name="field">目标字段的信息。</param>
		/// <param name="invoke">委托的方法信息。</param>
		/// <param name="invokeParams">委托的方法参数列表。</param>
		/// <returns>指定类型的委托，表示静态方法或实例字段。</returns>
		/// <exception cref="System.MethodAccessException">调用方无权访问
		/// <paramref name="field"/>。</exception>
		private static Delegate CreateOpenDelegate(Type type, FieldInfo field,
			MethodInfo invoke, ParameterInfo[] invokeParams)
		{
			// 检查参数数量。
			bool setField = invoke.ReturnType == typeof(void);
			int paramLen = field.IsStatic ? 0 : 1;
			paramLen += setField ? 1 : 0;
			if (invokeParams.Length == paramLen)
			{
				// 委托的参数列表。
				ParameterExpression[] paramList = invokeParams.ToExpressions();
				// 访问字段的实例对象。
				Expression instance = null;
				if (!field.IsStatic)
				{
					instance = paramList[0].ConvertType(field.DeclaringType);
					if (instance == null)
					{
						return null;
					}
				}
				Expression fieldExp = Expression.Field(instance, field);
				if (setField)
				{
					// 字段的设置值。
					Expression value = paramList[paramLen - 1].ConvertType(field.FieldType);
					if (value == null)
					{
						return null;
					}
					fieldExp = Expression.Assign(fieldExp, value);
				}
				fieldExp = GetReturn(fieldExp, invoke.ReturnType);
				if (fieldExp != null)
				{
					// 创建委托。
					return Expression.Lambda(type, fieldExp, paramList).Compile();
				}
			}
			return null;
		}

		#endregion // 从 FieldInfo 构造字段委托

		#region 从 FieldInfo 构造带有第一个参数的字段委托

		/// <summary>
		/// 使用指定的第一个参数创建表示获取或设置指定的静态或实例字段的指定类型的委托。 
		/// 如果委托具有返回值，则认为是获取字段，否则认为是设置字段。
		/// 支持参数的强制类型转换，参数声明可以与实际类型不同。
		/// </summary>
		/// <typeparam name="TDelegate">要创建的委托的类型。</typeparam>
		/// <param name="field">描述委托要表示的静态或实例字段的 
		/// <see cref="System.Reflection.PropertyInfo"/>。</param>
		/// <param name="firstArgument">委托要绑定到的对象，或为 <c>null</c>，
		/// 后者表示将 <paramref name="field"/> 视为 <c>static</c>。</param>
		/// <returns>指定类型的委托，表示获取或设置指定的静态或实例字段。</returns>
		/// <exception cref="System.ArgumentNullException"><paramref name="field"/> 为 <c>null</c>。</exception>
		/// <exception cref="System.ArgumentException"><typeparamref name="TDelegate"/> 不继承
		/// <see cref="System.MulticastDelegate"/>。</exception>
		/// <exception cref="System.ArgumentException">不能绑定 <paramref name="field"/>。</exception>
		/// <exception cref="System.MissingMethodException">未找到 <typeparamref name="TDelegate"/>
		/// 的 <c>Invoke</c> 方法。</exception>
		/// <exception cref="System.MethodAccessException">调用方无权访问
		/// <paramref name="field"/>。</exception>
		public static TDelegate CreateDelegate<TDelegate>(this FieldInfo field, object firstArgument)
			where TDelegate : class
		{
			return CreateDelegate(typeof(TDelegate), field, firstArgument, true) as TDelegate;
		}
		/// <summary>
		/// 使用指定的第一个参数和针对绑定失败的指定行为，创建表示获取或设置指定的静态或实例字段的指定类型的委托。 
		/// 如果委托具有返回值，则认为是获取字段，否则认为是设置字段。
		/// 支持参数的强制类型转换，参数声明可以与实际类型不同。
		/// </summary>
		/// <typeparam name="TDelegate">要创建的委托的类型。</typeparam>
		/// <param name="field">描述委托要表示的静态或实例字段的 
		/// <see cref="System.Reflection.PropertyInfo"/>。</param>
		/// <param name="firstArgument">委托要绑定到的对象，或为 <c>null</c>，
		/// 后者表示将 <paramref name="field"/> 视为 <c>static</c>。</param>
		/// <param name="throwOnBindFailure">为 <c>true</c>，表示无法绑定 <paramref name="field"/> 
		/// 时引发异常；否则为 <c>false</c>。</param>
		/// <returns>指定类型的委托，表示获取或设置指定的静态或实例字段。</returns>
		/// <exception cref="System.ArgumentNullException"><paramref name="field"/> 为 <c>null</c>。</exception>
		/// <exception cref="System.ArgumentException"><typeparamref name="TDelegate"/> 不继承
		/// <see cref="System.MulticastDelegate"/>。</exception>
		/// <exception cref="System.ArgumentException">不能绑定 <paramref name="field"/> 
		/// 且 <paramref name="throwOnBindFailure"/> 为 <c>true</c>。</exception>
		/// <exception cref="System.MissingMethodException">未找到 <typeparamref name="TDelegate"/>
		/// 的 <c>Invoke</c> 方法。</exception>
		/// <exception cref="System.MethodAccessException">调用方无权访问
		/// <paramref name="field"/>。</exception>
		public static TDelegate CreateDelegate<TDelegate>(this FieldInfo field, object firstArgument, bool throwOnBindFailure)
			where TDelegate : class
		{
			return CreateDelegate(typeof(TDelegate), field, firstArgument, throwOnBindFailure) as TDelegate;
		}
		/// <summary>
		/// 使用指定的第一个参数创建表示获取或设置指定的静态或实例字段的指定类型的委托。 
		/// 如果委托具有返回值，则认为是获取字段，否则认为是设置字段。
		/// 支持参数的强制类型转换，参数声明可以与实际类型不同。
		/// </summary>
		/// <param name="type">要创建的委托的类型。</param>
		/// <param name="field">描述委托要表示的静态或实例字段的 
		/// <see cref="System.Reflection.PropertyInfo"/>。</param>
		/// <param name="firstArgument">委托要绑定到的对象，或为 <c>null</c>，
		/// 后者表示将 <paramref name="field"/> 视为 <c>static</c>。</param>
		/// <returns>指定类型的委托，表示获取或设置指定的静态或实例字段。</returns>
		/// <exception cref="System.ArgumentNullException"><paramref name="type"/> 为 <c>null</c>。</exception>
		/// <exception cref="System.ArgumentNullException"><paramref name="field"/> 为 <c>null</c>。</exception>
		/// <exception cref="System.ArgumentException"><paramref name="type"/> 不继承
		/// <see cref="System.MulticastDelegate"/>。</exception>
		/// <exception cref="System.ArgumentException">不能绑定 <paramref name="field"/>。</exception>
		/// <exception cref="System.MissingMethodException">未找到 <paramref name="type"/>
		/// 的 <c>Invoke</c> 方法。</exception>
		/// <exception cref="System.MethodAccessException">调用方无权访问
		/// <paramref name="field"/>。</exception>
		public static Delegate CreateDelegate(Type type, FieldInfo field, object firstArgument)
		{
			return CreateDelegate(type, field, firstArgument, true);
		}
		/// <summary>
		/// 使用指定的第一个参数和针对绑定失败的指定行为，创建表示获取或设置指定的静态或实例字段的指定类型的委托。 
		/// 如果委托具有返回值，则认为是获取字段，否则认为是设置字段。
		/// 支持参数的强制类型转换，参数声明可以与实际类型不同。
		/// </summary>
		/// <param name="type">要创建的委托的类型。</param>
		/// <param name="field">描述委托要表示的静态或实例字段的 
		/// <see cref="System.Reflection.PropertyInfo"/>。</param>
		/// <param name="firstArgument">委托要绑定到的对象，或为 <c>null</c>，
		/// 后者表示将 <paramref name="field"/> 视为 <c>static</c>。</param>
		/// <param name="throwOnBindFailure">为 <c>true</c>，表示无法绑定 <paramref name="field"/> 
		/// 时引发异常；否则为 <c>false</c>。</param>
		/// <returns>指定类型的委托，表示获取或设置指定的静态或实例字段。</returns>
		/// <exception cref="System.ArgumentNullException"><paramref name="type"/> 为 <c>null</c>。</exception>
		/// <exception cref="System.ArgumentNullException"><paramref name="field"/> 为 <c>null</c>。</exception>
		/// <exception cref="System.ArgumentException"><paramref name="type"/> 不继承
		/// <see cref="System.MulticastDelegate"/>。</exception>
		/// <exception cref="System.ArgumentException">不能绑定 <paramref name="field"/> 
		/// 且 <paramref name="throwOnBindFailure"/> 为 <c>true</c>。</exception>
		/// <exception cref="System.MissingMethodException">未找到 <paramref name="type"/>
		/// 的 <c>Invoke</c> 方法。</exception>
		/// <exception cref="System.MethodAccessException">调用方无权访问
		/// <paramref name="field"/>。</exception>
		public static Delegate CreateDelegate(Type type, FieldInfo field, object firstArgument, bool throwOnBindFailure)
		{
			CommonExceptions.CheckArgumentNull(field, "field");
			CommonExceptions.CheckDelegateType(type, "type");
			MethodInfo invoke = type.GetMethod("Invoke");
			ParameterInfo[] invokeParams = invoke.GetParameters();
			// 尝试创建开放的字段委托。
			Delegate dlg = CreateOpenDelegate(type, field, invoke, invokeParams);
			if (dlg != null)
			{
				return dlg;
			}
			// 尝试创建封闭的字段委托。
			bool setField = invoke.ReturnType == typeof(void);
			// 检查参数数量。
			if (invokeParams.Length == ((field.IsStatic || !setField) ? 0 : 1))
			{
				if (firstArgument == null && !field.IsStatic)
				{
					// 通过空引用封闭的实例字段委托。
					// 委托的参数列表。
					ParameterExpression[] paramList = invokeParams.ToExpressions();
					Expression throwExp = Expression.Throw(Expression.New(typeof(NullReferenceException)));
					Expression fieldExp = Expression.Block(throwExp, Expression.Default(field.FieldType));
					dlg = Expression.Lambda(type, fieldExp, paramList).Compile();
				}
				else
				{
					// 通过第一个参数封闭的字段委托。
					dlg = CreateCloseDelegate(type, field, firstArgument, invoke, invokeParams);
				}
			}
			if (dlg == null && throwOnBindFailure)
			{
				throw CommonExceptions.BindTargetField("field");
			}
			return dlg;
		}
		/// <summary>
		/// 创建指定的静态或实例字段的指定类型的封闭字段委托。
		/// 支持参数的强制类型转换，参数声明可以与实际类型不同。
		/// </summary>
		/// <param name="type">要创建的委托的类型。</param>
		/// <param name="field">目标字段的信息。</param>
		/// <param name="firstArgument">委托表示的字段的第一个参数。</param>
		/// <param name="invoke">委托的方法信息。</param>
		/// <param name="invokeParams">委托的方法参数列表。</param>
		/// <returns>指定类型的委托，表示静态方法或实例字段。</returns>
		/// <exception cref="System.MethodAccessException">调用方无权访问
		/// <paramref name="field"/>。</exception>
		private static Delegate CreateCloseDelegate(Type type, FieldInfo field, object firstArgument,
			MethodInfo invoke, ParameterInfo[] invokeParams)
		{
			// 委托的参数列表。
			ParameterExpression[] paramList = invokeParams.ToExpressions();
			// 访问字段的实例对象。
			Expression instance = null;
			if (!field.IsStatic)
			{
				instance = Expression.Constant(firstArgument).ConvertType(field.DeclaringType);
				if (instance == null)
				{
					return null;
				}
			}
			Expression fieldExp = Expression.Field(instance, field);
			if (invoke.ReturnType == typeof(void))
			{
				// 字段的设置值。
				Expression value = null;
				if (field.IsStatic)
				{
					value = Expression.Constant(firstArgument).ConvertType(field.FieldType);
				}
				else
				{
					value = paramList[0].ConvertType(field.FieldType);
				}
				if (value == null)
				{
					return null;
				}
				fieldExp = Expression.Assign(fieldExp, value);
			}
			fieldExp = GetReturn(fieldExp, invoke.ReturnType);
			if (fieldExp != null)
			{
				// 创建委托。
				return Expression.Lambda(type, fieldExp, paramList).Compile();
			}
			return null;
		}

		#endregion // 从 FieldInfo 构造带有第一个参数的字段委托

		#region 从 Type 构造成员委托

		/// <summary>
		/// 构造函数的名称。
		/// </summary>
		private const string ConstructorName = ".type";
		/// <summary>
		/// 默认的绑定设置值。
		/// </summary>
		private const BindingFlags BinderDefault = BindingFlags.Static | BindingFlags.Instance |
			BindingFlags.Public | BindingFlags.NonPublic;
		/// <summary>
		/// 获取或设置字段的有效值掩码。
		/// </summary>
		private const BindingFlags BinderGetSetField = BindingFlags.GetField | BindingFlags.SetField;
		/// <summary>
		/// 获取或设置属性的有效值掩码。
		/// </summary>
		private const BindingFlags BinderGetSetProperty = BindingFlags.GetProperty | BindingFlags.SetProperty;
		/// <summary>
		/// 属性或方法的有效值掩码。
		/// </summary>
		private const BindingFlags BinderMethodOrProperty = BinderGetSetProperty | BindingFlags.InvokeMethod;
		/// <summary>
		/// 所有成员有效值掩码。
		/// </summary>
		private const BindingFlags BinderMemberMask = BinderGetSetField | BinderGetSetProperty |
			BindingFlags.InvokeMethod | BindingFlags.CreateInstance | BindingFlags.CreateInstance;
		/// <summary>
		/// 绑定设置值的有效值掩码。
		/// </summary>
		private const BindingFlags BinderMask = BindingFlags.IgnoreCase | BindingFlags.Static | BindingFlags.Instance |
			BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.FlattenHierarchy | BindingFlags.ExactBinding;
		/// <summary>
		/// 使用指定的名称创建用于表示静态或实例成员的指定类型的委托。
		/// 如果是实例成员，需要将实例对象作为委托的第一个参数。
		/// 对于属性和字段成员，如果委托具有返回值，则认为是获取属性或字段，否则认为是设置。
		/// 按照方法、属性、字段的顺序查找匹配的成员。
		/// 支持参数的强制类型转换，参数声明可以与实际类型不同。
		/// </summary>
		/// <typeparam name="TDelegate">要创建的委托的类型。</typeparam>
		/// <param name="target">表示实现成员的类的 <see cref="System.Type"/>。</param>
		/// <param name="memberName">委托要表示的成员的名称。</param> 
		/// <returns>指定类型的委托，表示指定的静态或实例成员。</returns>
		/// <exception cref="System.ArgumentNullException"><paramref name="target"/> 为 <c>null</c>。</exception>
		/// <exception cref="System.ArgumentNullException"><paramref name="memberName"/> 为 <c>null</c>。</exception>
		/// <exception cref="System.ArgumentException"><typeparamref name="TDelegate"/> 不继承
		/// <see cref="System.MulticastDelegate"/>。</exception>
		/// <exception cref="System.ArgumentException"><paramref name="target"/> 是一个开放式泛型类型。</exception>
		/// <exception cref="System.ArgumentException">无法绑定 <paramref name="memberName"/>。</exception>
		/// <exception cref="System.MissingMethodException">未找到 <typeparamref name="TDelegate"/>
		/// 的 <c>Invoke</c> 方法。</exception>
		/// <exception cref="System.MethodAccessException">调用方无权访问成员。</exception>
		/// <overloads>
		/// <summary>
		/// 使用指定的名称创建用于表示静态或实例成员的指定类型的委托。
		/// 对于属性和字段成员，如果委托具有返回值，则认为是获取属性或字段，否则认为是设置。
		/// 按照方法、属性、字段的顺序查找匹配的成员。
		/// 支持参数的强制类型转换，参数声明可以与实际类型不同。
		/// </summary>
		/// </overloads>
		public static Lazy<TDelegate> CreateDelegateLazy<TDelegate>(this Type target, string memberName)
			where TDelegate : class
		{
			return new Lazy<TDelegate>(() =>
				CreateDelegate(typeof(TDelegate), target, memberName, BinderDefault, true) as TDelegate);
		}
		/// <summary>
		/// 使用指定的名称和搜索方式，创建用于表示静态或实例成员的指定类型的委托。
		/// 如果是实例成员，需要将实例对象作为委托的第一个参数。
		/// 对于属性和字段成员，如果委托具有返回值，则认为是获取属性或字段，否则认为是设置。
		/// 按照方法、属性、字段的顺序查找匹配的成员。
		/// 支持参数的强制类型转换，参数声明可以与实际类型不同。
		/// 可以通过指定 <see cref="BindingFlags.InvokeMethod"/>，<see cref="BindingFlags.CreateInstance"/>，
		/// <see cref="BindingFlags.GetField"/>，<see cref="BindingFlags.SetField"/>，
		/// <see cref="BindingFlags.GetProperty"/>，<see cref="BindingFlags.SetProperty"/> 来选择要绑定到的成员类型。
		/// </summary>
		/// <typeparam name="TDelegate">要创建的委托的类型。</typeparam>
		/// <param name="target">表示实现成员的类的 <see cref="System.Type"/>。</param>
		/// <param name="memberName">委托要表示的成员的名称。</param> 
		/// <param name="bindingAttr">一个位屏蔽，由一个或多个指定搜索执行方式的 
		/// <see cref="System.Reflection.BindingFlags"/> 组成。</param>
		/// <returns>指定类型的委托，表示指定的静态或实例成员。</returns>
		/// <exception cref="System.ArgumentNullException"><paramref name="target"/> 为 <c>null</c>。</exception>
		/// <exception cref="System.ArgumentNullException"><paramref name="memberName"/> 为 <c>null</c>。</exception>
		/// <exception cref="System.ArgumentException"><typeparamref name="TDelegate"/> 不继承
		/// <see cref="System.MulticastDelegate"/>。</exception>
		/// <exception cref="System.ArgumentException"><paramref name="target"/> 是一个开放式泛型类型。</exception>
		/// <exception cref="System.ArgumentException">无法绑定 <paramref name="memberName"/>。</exception>
		/// <exception cref="System.MissingMethodException">未找到 <typeparamref name="TDelegate"/>
		/// 的 <c>Invoke</c> 方法。</exception>
		/// <exception cref="System.MethodAccessException">调用方无权访问成员。</exception>
		public static Lazy<TDelegate> CreateDelegateLazy<TDelegate>(this Type target, string memberName, BindingFlags bindingAttr)
			where TDelegate : class
		{
			return new Lazy<TDelegate>(() =>
				CreateDelegate(typeof(TDelegate), target, memberName, bindingAttr, true) as TDelegate);
		}
		/// <summary>
		/// 使用指定的名称、搜索方式和针对绑定失败的指定行为，创建用于表示静态或实例成员的指定类型的委托。
		/// 如果是实例成员，需要将实例对象作为委托的第一个参数。
		/// 对于属性和字段成员，如果委托具有返回值，则认为是获取属性或字段，否则认为是设置。
		/// 按照方法、属性、字段的顺序查找匹配的成员。
		/// 支持参数的强制类型转换，参数声明可以与实际类型不同。
		/// 可以通过指定 <see cref="BindingFlags.InvokeMethod"/>，<see cref="BindingFlags.CreateInstance"/>，
		/// <see cref="BindingFlags.GetField"/>，<see cref="BindingFlags.SetField"/>，
		/// <see cref="BindingFlags.GetProperty"/>，<see cref="BindingFlags.SetProperty"/> 来选择要绑定到的成员类型。
		/// </summary>
		/// <typeparam name="TDelegate">要创建的委托的类型。</typeparam>
		/// <param name="target">表示实现成员的类的 <see cref="System.Type"/>。</param>
		/// <param name="memberName">委托要表示的成员的名称。</param> 
		/// <param name="bindingAttr">一个位屏蔽，由一个或多个指定搜索执行方式的 
		/// <see cref="System.Reflection.BindingFlags"/> 组成。</param>
		/// <param name="throwOnBindFailure">为 <c>true</c>，表示无法绑定 <paramref name="memberName"/> 
		/// 时引发异常；否则为 <c>false</c>。</param>
		/// <returns>指定类型的委托，表示指定的静态或实例成员。</returns>
		/// <exception cref="System.ArgumentNullException"><paramref name="target"/> 为 <c>null</c>。</exception>
		/// <exception cref="System.ArgumentNullException"><paramref name="memberName"/> 为 <c>null</c>。</exception>
		/// <exception cref="System.ArgumentException"><typeparamref name="TDelegate"/> 不继承
		/// <see cref="System.MulticastDelegate"/>。</exception>
		/// <exception cref="System.ArgumentException"><paramref name="target"/> 是一个开放式泛型类型。</exception>
		/// <exception cref="System.ArgumentException">无法绑定 <paramref name="memberName"/>
		/// 且 <paramref name="throwOnBindFailure"/> 为 <c>true</c>。</exception>
		/// <exception cref="System.MissingMethodException">未找到 <typeparamref name="TDelegate"/>
		/// 的 <c>Invoke</c> 方法。</exception>
		/// <exception cref="System.MethodAccessException">调用方无权访问成员。</exception>
		public static Lazy<TDelegate> CreateDelegateLazy<TDelegate>(this Type target, string memberName, BindingFlags bindingAttr,
			bool throwOnBindFailure)
			where TDelegate : class
		{
			return new Lazy<TDelegate>(() =>
				CreateDelegate(typeof(TDelegate), target, memberName, bindingAttr, throwOnBindFailure) as TDelegate);
		}
		/// <summary>
		/// 使用指定的名称创建用于表示静态或实例成员的指定类型的委托。
		/// 如果是实例成员，需要将实例对象作为委托的第一个参数。
		/// 对于属性和字段成员，如果委托具有返回值，则认为是获取属性或字段，否则认为是设置。
		/// 按照方法、属性、字段的顺序查找匹配的成员。
		/// 支持参数的强制类型转换，参数声明可以与实际类型不同。
		/// </summary>
		/// <typeparam name="TDelegate">要创建的委托的类型。</typeparam>
		/// <param name="target">表示实现成员的类的 <see cref="System.Type"/>。</param>
		/// <param name="memberName">委托要表示的成员的名称。</param> 
		/// <returns>指定类型的委托，表示指定的静态或实例成员。</returns>
		/// <exception cref="System.ArgumentNullException"><paramref name="target"/> 为 <c>null</c>。</exception>
		/// <exception cref="System.ArgumentNullException"><paramref name="memberName"/> 为 <c>null</c>。</exception>
		/// <exception cref="System.ArgumentException"><typeparamref name="TDelegate"/> 不继承
		/// <see cref="System.MulticastDelegate"/>。</exception>
		/// <exception cref="System.ArgumentException"><paramref name="target"/> 是一个开放式泛型类型。</exception>
		/// <exception cref="System.ArgumentException">无法绑定 <paramref name="memberName"/>。</exception>
		/// <exception cref="System.MissingMethodException">未找到 <typeparamref name="TDelegate"/>
		/// 的 <c>Invoke</c> 方法。</exception>
		/// <exception cref="System.MethodAccessException">调用方无权访问成员。</exception>
		public static TDelegate CreateDelegate<TDelegate>(this Type target, string memberName)
			where TDelegate : class
		{
			return CreateDelegate(typeof(TDelegate), target, memberName, BinderDefault, true) as TDelegate;
		}
		/// <summary>
		/// 使用指定的名称和搜索方式，创建用于表示静态或实例成员的指定类型的委托。
		/// 如果是实例成员，需要将实例对象作为委托的第一个参数。
		/// 对于属性和字段成员，如果委托具有返回值，则认为是获取属性或字段，否则认为是设置。
		/// 按照方法、属性、字段的顺序查找匹配的成员。
		/// 支持参数的强制类型转换，参数声明可以与实际类型不同。
		/// 可以通过指定 <see cref="BindingFlags.InvokeMethod"/>，<see cref="BindingFlags.CreateInstance"/>，
		/// <see cref="BindingFlags.GetField"/>，<see cref="BindingFlags.SetField"/>，
		/// <see cref="BindingFlags.GetProperty"/>，<see cref="BindingFlags.SetProperty"/> 来选择要绑定到的成员类型。
		/// </summary>
		/// <typeparam name="TDelegate">要创建的委托的类型。</typeparam>
		/// <param name="target">表示实现成员的类的 <see cref="System.Type"/>。</param>
		/// <param name="memberName">委托要表示的成员的名称。</param> 
		/// <param name="bindingAttr">一个位屏蔽，由一个或多个指定搜索执行方式的 
		/// <see cref="System.Reflection.BindingFlags"/> 组成。</param>
		/// <returns>指定类型的委托，表示指定的静态或实例成员。</returns>
		/// <exception cref="System.ArgumentNullException"><paramref name="target"/> 为 <c>null</c>。</exception>
		/// <exception cref="System.ArgumentNullException"><paramref name="memberName"/> 为 <c>null</c>。</exception>
		/// <exception cref="System.ArgumentException"><typeparamref name="TDelegate"/> 不继承
		/// <see cref="System.MulticastDelegate"/>。</exception>
		/// <exception cref="System.ArgumentException"><paramref name="target"/> 是一个开放式泛型类型。</exception>
		/// <exception cref="System.ArgumentException">无法绑定 <paramref name="memberName"/>。</exception>
		/// <exception cref="System.MissingMethodException">未找到 <typeparamref name="TDelegate"/>
		/// 的 <c>Invoke</c> 方法。</exception>
		/// <exception cref="System.MethodAccessException">调用方无权访问成员。</exception>
		public static TDelegate CreateDelegate<TDelegate>(this Type target, string memberName, BindingFlags bindingAttr)
			where TDelegate : class
		{
			return CreateDelegate(typeof(TDelegate), target, memberName, bindingAttr, true) as TDelegate;
		}
		/// <summary>
		/// 使用指定的名称、搜索方式和针对绑定失败的指定行为，创建用于表示静态或实例成员的指定类型的委托。
		/// 如果是实例成员，需要将实例对象作为委托的第一个参数。
		/// 对于属性和字段成员，如果委托具有返回值，则认为是获取属性或字段，否则认为是设置。
		/// 按照方法、属性、字段的顺序查找匹配的成员。
		/// 支持参数的强制类型转换，参数声明可以与实际类型不同。
		/// 可以通过指定 <see cref="BindingFlags.InvokeMethod"/>，<see cref="BindingFlags.CreateInstance"/>，
		/// <see cref="BindingFlags.GetField"/>，<see cref="BindingFlags.SetField"/>，
		/// <see cref="BindingFlags.GetProperty"/>，<see cref="BindingFlags.SetProperty"/> 来选择要绑定到的成员类型。
		/// </summary>
		/// <typeparam name="TDelegate">要创建的委托的类型。</typeparam>
		/// <param name="target">表示实现成员的类的 <see cref="System.Type"/>。</param>
		/// <param name="memberName">委托要表示的成员的名称。</param> 
		/// <param name="bindingAttr">一个位屏蔽，由一个或多个指定搜索执行方式的 
		/// <see cref="System.Reflection.BindingFlags"/> 组成。</param>
		/// <param name="throwOnBindFailure">为 <c>true</c>，表示无法绑定 <paramref name="memberName"/> 
		/// 时引发异常；否则为 <c>false</c>。</param>
		/// <returns>指定类型的委托，表示指定的静态或实例成员。</returns>
		/// <exception cref="System.ArgumentNullException"><paramref name="target"/> 为 <c>null</c>。</exception>
		/// <exception cref="System.ArgumentNullException"><paramref name="memberName"/> 为 <c>null</c>。</exception>
		/// <exception cref="System.ArgumentException"><typeparamref name="TDelegate"/> 不继承
		/// <see cref="System.MulticastDelegate"/>。</exception>
		/// <exception cref="System.ArgumentException"><paramref name="target"/> 是一个开放式泛型类型。</exception>
		/// <exception cref="System.ArgumentException">无法绑定 <paramref name="memberName"/>
		/// 且 <paramref name="throwOnBindFailure"/> 为 <c>true</c>。</exception>
		/// <exception cref="System.MissingMethodException">未找到 <typeparamref name="TDelegate"/>
		/// 的 <c>Invoke</c> 方法。</exception>
		/// <exception cref="System.MethodAccessException">调用方无权访问成员。</exception>
		public static TDelegate CreateDelegate<TDelegate>(this Type target, string memberName, BindingFlags bindingAttr,
			bool throwOnBindFailure)
			where TDelegate : class
		{
			return CreateDelegate(typeof(TDelegate), target, memberName, bindingAttr, throwOnBindFailure) as TDelegate;
		}
		/// <summary>
		/// 使用指定的名称创建用于表示静态或实例成员的指定类型的委托。
		/// 如果是实例成员，需要将实例对象作为委托的第一个参数。
		/// 对于属性和字段成员，如果委托具有返回值，则认为是获取属性或字段，否则认为是设置。
		/// 按照方法、属性、字段的顺序查找匹配的成员。
		/// 支持参数的强制类型转换，参数声明可以与实际类型不同。
		/// </summary>
		/// <param name="type">要创建的委托的类型。</param>
		/// <param name="target">表示实现成员的类的 <see cref="System.Type"/>。</param>
		/// <param name="memberName">委托要表示的成员的名称。</param> 
		/// <returns>指定类型的委托，表示指定的静态或实例成员。</returns>
		/// <exception cref="System.ArgumentNullException"><paramref name="type"/> 为 <c>null</c>。</exception>
		/// <exception cref="System.ArgumentNullException"><paramref name="target"/> 为 <c>null</c>。</exception>
		/// <exception cref="System.ArgumentNullException"><paramref name="memberName"/> 为 <c>null</c>。</exception>
		/// <exception cref="System.ArgumentException"><paramref name="type"/> 不继承
		/// <see cref="System.MulticastDelegate"/>。</exception>
		/// <exception cref="System.ArgumentException"><paramref name="target"/> 是一个开放式泛型类型。</exception>
		/// <exception cref="System.ArgumentException">无法绑定 <paramref name="memberName"/>。</exception>
		/// <exception cref="System.MissingMethodException">未找到 <paramref name="type"/>
		/// 的 <c>Invoke</c> 方法。</exception>
		/// <exception cref="System.MethodAccessException">调用方无权访问成员。</exception>
		public static Delegate CreateDelegate(Type type, Type target, string memberName)
		{
			return CreateDelegate(type, target, memberName, BinderDefault, true);
		}
		/// <summary>
		/// 使用指定的名称和搜索方式，创建用于表示静态或实例成员的指定类型的委托。
		/// 如果是实例成员，需要将实例对象作为委托的第一个参数。
		/// 对于属性和字段成员，如果委托具有返回值，则认为是获取属性或字段，否则认为是设置。
		/// 按照方法、属性、字段的顺序查找匹配的成员。
		/// 支持参数的强制类型转换，参数声明可以与实际类型不同。
		/// 可以通过指定 <see cref="BindingFlags.InvokeMethod"/>，<see cref="BindingFlags.CreateInstance"/>，
		/// <see cref="BindingFlags.GetField"/>，<see cref="BindingFlags.SetField"/>，
		/// <see cref="BindingFlags.GetProperty"/>，<see cref="BindingFlags.SetProperty"/> 来选择要绑定到的成员类型。
		/// </summary>
		/// <param name="type">要创建的委托的类型。</param>
		/// <param name="target">表示实现成员的类的 <see cref="System.Type"/>。</param>
		/// <param name="memberName">委托要表示的成员的名称。</param> 
		/// <param name="bindingAttr">一个位屏蔽，由一个或多个指定搜索执行方式的 
		/// <see cref="System.Reflection.BindingFlags"/> 组成。</param>
		/// <returns>指定类型的委托，表示指定的静态或实例成员。</returns>
		/// <exception cref="System.ArgumentNullException"><paramref name="type"/> 为 <c>null</c>。</exception>
		/// <exception cref="System.ArgumentNullException"><paramref name="target"/> 为 <c>null</c>。</exception>
		/// <exception cref="System.ArgumentNullException"><paramref name="memberName"/> 为 <c>null</c>。</exception>
		/// <exception cref="System.ArgumentException"><paramref name="type"/> 不继承
		/// <see cref="System.MulticastDelegate"/>。</exception>
		/// <exception cref="System.ArgumentException"><paramref name="target"/> 是一个开放式泛型类型。</exception>
		/// <exception cref="System.ArgumentException">无法绑定 <paramref name="memberName"/>。</exception>
		/// <exception cref="System.MissingMethodException">未找到 <paramref name="type"/>
		/// 的 <c>Invoke</c> 方法。</exception>
		/// <exception cref="System.MethodAccessException">调用方无权访问成员。</exception>
		public static Delegate CreateDelegate(Type type, Type target, string memberName, BindingFlags bindingAttr)
		{
			return CreateDelegate(type, target, memberName, bindingAttr, true);
		}
		/// <summary>
		/// 使用指定的名称、搜索方式和针对绑定失败的指定行为，创建用于表示静态或实例成员的指定类型的委托。
		/// 如果是实例成员，需要将实例对象作为委托的第一个参数。
		/// 对于属性和字段成员，如果委托具有返回值，则认为是获取属性或字段，否则认为是设置。
		/// 按照方法、属性、字段的顺序查找匹配的成员。
		/// 支持参数的强制类型转换，参数声明可以与实际类型不同。
		/// 可以通过指定 <see cref="BindingFlags.InvokeMethod"/>，<see cref="BindingFlags.CreateInstance"/>，
		/// <see cref="BindingFlags.GetField"/>，<see cref="BindingFlags.SetField"/>，
		/// <see cref="BindingFlags.GetProperty"/>，<see cref="BindingFlags.SetProperty"/> 来选择要绑定到的成员类型。
		/// </summary>
		/// <param name="type">要创建的委托的类型。</param>
		/// <param name="target">表示实现成员的类的 <see cref="System.Type"/>。</param>
		/// <param name="memberName">委托要表示的成员的名称。</param> 
		/// <param name="bindingAttr">一个位屏蔽，由一个或多个指定搜索执行方式的 
		/// <see cref="System.Reflection.BindingFlags"/> 组成。</param>
		/// <param name="throwOnBindFailure">为 <c>true</c>，表示无法绑定 <paramref name="memberName"/> 
		/// 时引发异常；否则为 <c>false</c>。</param>
		/// <returns>指定类型的委托，表示指定的静态或实例成员。</returns>
		/// <exception cref="System.ArgumentNullException"><paramref name="type"/> 为 <c>null</c>。</exception>
		/// <exception cref="System.ArgumentNullException"><paramref name="target"/> 为 <c>null</c>。</exception>
		/// <exception cref="System.ArgumentNullException"><paramref name="memberName"/> 为 <c>null</c>。</exception>
		/// <exception cref="System.ArgumentException"><paramref name="type"/> 不继承
		/// <see cref="System.MulticastDelegate"/>。</exception>
		/// <exception cref="System.ArgumentException"><paramref name="target"/> 是一个开放式泛型类型。</exception>
		/// <exception cref="System.ArgumentException">无法绑定 <paramref name="memberName"/>
		/// 且 <paramref name="throwOnBindFailure"/> 为 <c>true</c>。</exception>
		/// <exception cref="System.MissingMethodException">未找到 <paramref name="type"/>
		/// 的 <c>Invoke</c> 方法。</exception>
		/// <exception cref="System.MethodAccessException">调用方无权访问成员。</exception>
		public static Delegate CreateDelegate(Type type, Type target, string memberName, BindingFlags bindingAttr,
			bool throwOnBindFailure)
		{
			CommonExceptions.CheckArgumentNull(memberName, "memberName");
			CommonExceptions.CheckDelegateType(type, "type");
			CheckTargetType(target, "target");
			MethodInfo invoke = type.GetMethod("Invoke");
			ParameterInfo[] paramInfos = invoke.GetParameters();
			Type[] types = GetParameterTypes(paramInfos, 0, 0, 0);
			BindingFlags memberFlags = bindingAttr & BinderMemberMask;
			if (memberFlags == BindingFlags.Default)
			{
				// 未设置 MemberMask 的情况下，查找所有成员。
				memberFlags = BinderMemberMask;
			}
			else
			{
				// 清除 bindingAttr 中的成员设置。
				bindingAttr &= ~BinderMemberMask;
			}
			BindingFlags instancecBindingAttr = bindingAttr & ~BindingFlags.Static;
			Delegate dlg = null;
			if (memberName.Equals(ConstructorName, StringComparison.OrdinalIgnoreCase))
			{
				// 查找构造函数成员，此时注意只搜索实例方法。
				ConstructorInfo ctor = target.GetConstructor(instancecBindingAttr | BindingFlags.Instance,
					PowerBinder.CastBinder, types, null);
				if (ctor != null)
				{
					dlg = CreateDelegate(type, ctor, false);
				}
			}
			else
			{
				// 查找其它成员。
				Type[] instanceTypes = null;
				bool containsStaticMember = (bindingAttr & BindingFlags.Static) == BindingFlags.Static;
				bool containsInstnceMember = (bindingAttr & BindingFlags.Instance) == BindingFlags.Instance;
				if (containsInstnceMember)
				{
					// 构造查找实例方法用的参数列表。
					if (types.Length - 1 < 0 ||
						(memberFlags & BinderMethodOrProperty) == BindingFlags.Default)
					{
						// 参数个数为 0，不能是实例成员。
						// 没有方法或属性调用，也不用区分实例成员。
						containsInstnceMember = false;
					}
					else
					{
						instanceTypes = new Type[types.Length - 1];
						for (int i = 0; i < instanceTypes.Length; i++)
						{
							instanceTypes[i] = types[i + 1];
						}
					}
				}
				BindingFlags staticBindingAttr = bindingAttr & ~BindingFlags.Instance;
				if ((memberFlags & BindingFlags.InvokeMethod) == BindingFlags.InvokeMethod)
				{
					// 查找静态方法成员。
					if (containsStaticMember)
					{
						dlg = CreateMethodDelegate(type, target, memberName, null, staticBindingAttr, types);
					}
					// 查找实例方法成员。
					if (dlg == null && containsInstnceMember)
					{
						dlg = CreateMethodDelegate(type, target, memberName, null, instancecBindingAttr, instanceTypes);
					}
				}
				if (dlg == null && (memberFlags & BinderGetSetProperty) != BindingFlags.Default)
				{
					// 查找静态属性成员。
					if (containsStaticMember)
					{
						dlg = CreatePropertyDelegate(type, target, memberName, null, staticBindingAttr,
							invoke.ReturnType, types);
					}
					// 查找实例属性成员。
					if (dlg == null && containsInstnceMember)
					{
						dlg = CreatePropertyDelegate(type, target, memberName, null, instancecBindingAttr,
							invoke.ReturnType, instanceTypes);
					}
				}
				// 查找字段成员。
				if (dlg == null && (memberFlags & BinderGetSetField) != BindingFlags.Default)
				{
					FieldInfo field = target.GetField(memberName, bindingAttr);
					if (field != null)
					{
						dlg = CreateDelegate(type, field, false);
					}
				}
			}
			if (dlg != null)
			{
				return dlg;
			}
			if (throwOnBindFailure)
			{
				throw CommonExceptions.BindTargetMethod("memberName");
			}
			return null;
		}
		/// <summary>
		/// 使用指定的搜索方式创建用于表示静态或实例方法的指定类型的委托。
		/// </summary>
		/// <param name="type">要创建的委托的类型。</param>
		/// <param name="target">表示实现成员的类的 <see cref="System.Type"/>。</param>
		/// <param name="methodName">方法的名称。</param>
		/// <param name="firstArgument">委托要绑定到的对象。</param>
		/// <param name="bindingAttr">一个位屏蔽，由一个或多个指定搜索执行方式的 
		/// <see cref="System.Reflection.BindingFlags"/> 组成。</param>
		/// <param name="types">方法的签名。</param>
		/// <returns>指定类型的委托，表示指定的静态或实例方法。</returns>
		private static Delegate CreateMethodDelegate(Type type, Type target, string methodName, object firstArgument,
			BindingFlags bindingAttr, Type[] types)
		{
			MethodInfo method = target.GetMethod(methodName, bindingAttr, PowerBinder.CastBinder, types, null);
			if (method != null)
			{
				if (firstArgument == null)
				{
					return CreateDelegate(type, (MethodBase)method, false);
				}
				else
				{
					return CreateDelegate(type, method, firstArgument, false);
				}
			}
			return null;
		}
		/// <summary>
		/// 使用指定的搜索方式创建用于表示静态或实例属性的指定类型的委托。
		/// </summary>
		/// <param name="type">要创建的委托的类型。</param>
		/// <param name="target">表示实现成员的类的 <see cref="System.Type"/>。</param>
		/// <param name="propertyName">方法的名称。</param>
		/// <param name="firstArgument">委托要绑定到的对象。</param>
		/// <param name="bindingAttr">一个位屏蔽，由一个或多个指定搜索执行方式的 
		/// <see cref="System.Reflection.BindingFlags"/> 组成。</param>
		/// <param name="propType">属性的类型。</param>
		/// <param name="types">方法的签名。</param>
		/// <returns>指定类型的委托，表示指定的静态或属性方法。</returns>
		private static Delegate CreatePropertyDelegate(Type type, Type target, string propertyName,
			object firstArgument, BindingFlags bindingAttr, Type propType, Type[] types)
		{
			PropertyInfo property = null;
			if (propType == typeof(void))
			{
				// 是设置属性，将第一个参数作为属性类型。
				propType = types[0];
				if (types.Length == 1)
				{
					types = Type.EmptyTypes;
				}
				else
				{
					Type[] newTypes = new Type[types.Length - 1];
					for (int i = 0; i < newTypes.Length; i++)
					{
						newTypes[i] = types[i + 1];
					}
					types = newTypes;
				}
				property = target.GetProperty(propertyName, bindingAttr, PowerBinder.CastBinder,
					propType, types, null);
			}
			else
			{
				// 是获取属性。
				property = target.GetProperty(propertyName, bindingAttr, PowerBinder.CastBinder,
					propType, types, null);
			}
			if (property != null)
			{
				if (firstArgument == null)
				{
					return CreateDelegate(type, property, false);
				}
				else
				{
					return CreateDelegate(type, property, firstArgument, false);
				}
			}
			return null;
		}

		#endregion // 从 Type 构造成员委托

		#region 从 Type 构造带有第一个参数的成员委托

		/// <summary>
		/// 使用指定的名称和第一个参数，创建用于表示静态或实例成员的指定类型的委托。
		/// 如果 <paramref name="firstArgument"/> 不为 <c>null</c>，则搜索实例成员，
		/// 并将 <paramref name="firstArgument"/> 作为实例。如果为 <c>null</c>，则搜索静态成员。
		/// 对于属性和字段成员，如果委托具有返回值，则认为是获取属性或字段，否则认为是设置。
		/// 按照方法、属性、字段的顺序查找匹配的成员。
		/// 支持参数的强制类型转换，参数声明可以与实际类型不同。
		/// 可以通过指定 <see cref="BindingFlags.InvokeMethod"/>，<see cref="BindingFlags.CreateInstance"/>，
		/// <see cref="BindingFlags.GetField"/>，<see cref="BindingFlags.SetField"/>，
		/// <see cref="BindingFlags.GetProperty"/>，<see cref="BindingFlags.SetProperty"/> 来选择要绑定到的成员类型。
		/// </summary>
		/// <typeparam name="TDelegate">要创建的委托的类型。</typeparam>
		/// <param name="target">表示实现成员的类的 <see cref="System.Type"/>。</param>
		/// <param name="memberName">委托要表示的成员的名称。</param> 
		/// <param name="firstArgument">委托要绑定到的对象，或为 <c>null</c>，
		/// 后者表示将 <paramref name="memberName"/> 视为 <c>static</c>。</param>
		/// <returns>指定类型的委托，表示指定的静态或实例成员。</returns>
		/// <exception cref="System.ArgumentNullException"><paramref name="target"/> 为 <c>null</c>。</exception>
		/// <exception cref="System.ArgumentNullException"><paramref name="memberName"/> 为 <c>null</c>。</exception>
		/// <exception cref="System.ArgumentException"><typeparamref name="TDelegate"/> 不继承
		/// <see cref="System.MulticastDelegate"/>。</exception>
		/// <exception cref="System.ArgumentException"><paramref name="target"/> 是一个开放式泛型类型。</exception>
		/// <exception cref="System.ArgumentException">无法绑定 <paramref name="memberName"/>。</exception>
		/// <exception cref="System.MissingMethodException">未找到 <typeparamref name="TDelegate"/>
		/// 的 <c>Invoke</c> 方法。</exception>
		/// <exception cref="System.MethodAccessException">调用方无权访问成员。</exception>
		public static Lazy<TDelegate> CreateDelegateLazy<TDelegate>(this Type target, string memberName, object firstArgument)
			where TDelegate : class
		{
			return new Lazy<TDelegate>(() =>
				CreateDelegate(typeof(TDelegate), target, memberName, firstArgument, BinderDefault, true) as TDelegate);
		}
		/// <summary>
		/// 使用指定的名称、第一个参数和搜索方式，创建用于表示静态或实例成员的指定类型的委托。
		/// 如果 <paramref name="firstArgument"/> 不为 <c>null</c>，则搜索实例成员，
		/// 并将 <paramref name="firstArgument"/> 作为实例。如果为 <c>null</c>，则搜索静态成员。
		/// 对于属性和字段成员，如果委托具有返回值，则认为是获取属性或字段，否则认为是设置。
		/// 按照方法、属性、字段的顺序查找匹配的成员。
		/// 支持参数的强制类型转换，参数声明可以与实际类型不同。
		/// 可以通过指定 <see cref="BindingFlags.InvokeMethod"/>，<see cref="BindingFlags.CreateInstance"/>，
		/// <see cref="BindingFlags.GetField"/>，<see cref="BindingFlags.SetField"/>，
		/// <see cref="BindingFlags.GetProperty"/>，<see cref="BindingFlags.SetProperty"/> 来选择要绑定到的成员类型。
		/// </summary>
		/// <typeparam name="TDelegate">要创建的委托的类型。</typeparam>
		/// <param name="target">表示实现成员的类的 <see cref="System.Type"/>。</param>
		/// <param name="memberName">委托要表示的成员的名称。</param> 
		/// <param name="firstArgument">委托要绑定到的对象，或为 <c>null</c>，
		/// 后者表示将 <paramref name="memberName"/> 视为 <c>static</c>。</param>
		/// <param name="bindingAttr">一个位屏蔽，由一个或多个指定搜索执行方式的 
		/// <see cref="System.Reflection.BindingFlags"/> 组成。</param>
		/// <returns>指定类型的委托，表示指定的静态或实例成员。</returns>
		/// <exception cref="System.ArgumentNullException"><paramref name="target"/> 为 <c>null</c>。</exception>
		/// <exception cref="System.ArgumentNullException"><paramref name="memberName"/> 为 <c>null</c>。</exception>
		/// <exception cref="System.ArgumentException"><typeparamref name="TDelegate"/> 不继承
		/// <see cref="System.MulticastDelegate"/>。</exception>
		/// <exception cref="System.ArgumentException"><paramref name="target"/> 是一个开放式泛型类型。</exception>
		/// <exception cref="System.ArgumentException">无法绑定 <paramref name="memberName"/>。</exception>
		/// <exception cref="System.MissingMethodException">未找到 <typeparamref name="TDelegate"/>
		/// 的 <c>Invoke</c> 方法。</exception>
		/// <exception cref="System.MethodAccessException">调用方无权访问成员。</exception>
		public static Lazy<TDelegate> CreateDelegateLazy<TDelegate>(this Type target, string memberName,
			object firstArgument, BindingFlags bindingAttr)
			where TDelegate : class
		{
			return new Lazy<TDelegate>(() =>
				CreateDelegate(typeof(TDelegate), target, memberName, firstArgument, bindingAttr, true) as TDelegate);
		}
		/// <summary>
		/// 使用指定的名称、第一个参数、搜索方式和针对绑定失败的指定行为，创建用于表示静态或实例成员的指定类型的委托。
		/// 如果 <paramref name="firstArgument"/> 不为 <c>null</c>，则搜索实例成员，
		/// 并将 <paramref name="firstArgument"/> 作为实例。如果为 <c>null</c>，则搜索静态成员。
		/// 对于属性和字段成员，如果委托具有返回值，则认为是获取属性或字段，否则认为是设置。
		/// 按照方法、属性、字段的顺序查找匹配的成员。
		/// 支持参数的强制类型转换，参数声明可以与实际类型不同。
		/// 可以通过指定 <see cref="BindingFlags.InvokeMethod"/>，<see cref="BindingFlags.CreateInstance"/>，
		/// <see cref="BindingFlags.GetField"/>，<see cref="BindingFlags.SetField"/>，
		/// <see cref="BindingFlags.GetProperty"/>，<see cref="BindingFlags.SetProperty"/> 来选择要绑定到的成员类型。
		/// </summary>
		/// <typeparam name="TDelegate">要创建的委托的类型。</typeparam>
		/// <param name="target">表示实现成员的类的 <see cref="System.Type"/>。</param>
		/// <param name="memberName">委托要表示的成员的名称。</param> 
		/// <param name="firstArgument">委托要绑定到的对象，或为 <c>null</c>，
		/// 后者表示将 <paramref name="memberName"/> 视为 <c>static</c>。</param>
		/// <param name="bindingAttr">一个位屏蔽，由一个或多个指定搜索执行方式的 
		/// <see cref="System.Reflection.BindingFlags"/> 组成。</param>
		/// <param name="throwOnBindFailure">为 <c>true</c>，表示无法绑定 <paramref name="memberName"/> 
		/// 时引发异常；否则为 <c>false</c>。</param>
		/// <returns>指定类型的委托，表示指定的静态或实例成员。</returns>
		/// <exception cref="System.ArgumentNullException"><paramref name="target"/> 为 <c>null</c>。</exception>
		/// <exception cref="System.ArgumentNullException"><paramref name="memberName"/> 为 <c>null</c>。</exception>
		/// <exception cref="System.ArgumentException"><typeparamref name="TDelegate"/> 不继承
		/// <see cref="System.MulticastDelegate"/>。</exception>
		/// <exception cref="System.ArgumentException"><paramref name="target"/> 是一个开放式泛型类型。</exception>
		/// <exception cref="System.ArgumentException">无法绑定 <paramref name="memberName"/>
		/// 且 <paramref name="throwOnBindFailure"/> 为 <c>true</c>。</exception>
		/// <exception cref="System.MissingMethodException">未找到 <typeparamref name="TDelegate"/>
		/// 的 <c>Invoke</c> 方法。</exception>
		/// <exception cref="System.MethodAccessException">调用方无权访问成员。</exception>
		public static Lazy<TDelegate> CreateDelegateLazy<TDelegate>(this Type target, string memberName,
			object firstArgument, BindingFlags bindingAttr, bool throwOnBindFailure)
			where TDelegate : class
		{
			return new Lazy<TDelegate>(() =>
				CreateDelegate(typeof(TDelegate), target, memberName, firstArgument, bindingAttr, throwOnBindFailure) as TDelegate);
		}
		/// <summary>
		/// 使用指定的名称和第一个参数，创建用于表示静态或实例成员的指定类型的委托。
		/// 如果 <paramref name="firstArgument"/> 不为 <c>null</c>，则搜索实例成员，
		/// 并将 <paramref name="firstArgument"/> 作为实例。如果为 <c>null</c>，则搜索静态成员。
		/// 对于属性和字段成员，如果委托具有返回值，则认为是获取属性或字段，否则认为是设置。
		/// 按照方法、属性、字段的顺序查找匹配的成员。
		/// 支持参数的强制类型转换，参数声明可以与实际类型不同。
		/// 可以通过指定 <see cref="BindingFlags.InvokeMethod"/>，<see cref="BindingFlags.CreateInstance"/>，
		/// <see cref="BindingFlags.GetField"/>，<see cref="BindingFlags.SetField"/>，
		/// <see cref="BindingFlags.GetProperty"/>，<see cref="BindingFlags.SetProperty"/> 来选择要绑定到的成员类型。
		/// </summary>
		/// <typeparam name="TDelegate">要创建的委托的类型。</typeparam>
		/// <param name="target">表示实现成员的类的 <see cref="System.Type"/>。</param>
		/// <param name="memberName">委托要表示的成员的名称。</param> 
		/// <param name="firstArgument">委托要绑定到的对象，或为 <c>null</c>，
		/// 后者表示将 <paramref name="memberName"/> 视为 <c>static</c>。</param>
		/// <returns>指定类型的委托，表示指定的静态或实例成员。</returns>
		/// <exception cref="System.ArgumentNullException"><paramref name="target"/> 为 <c>null</c>。</exception>
		/// <exception cref="System.ArgumentNullException"><paramref name="memberName"/> 为 <c>null</c>。</exception>
		/// <exception cref="System.ArgumentException"><typeparamref name="TDelegate"/> 不继承
		/// <see cref="System.MulticastDelegate"/>。</exception>
		/// <exception cref="System.ArgumentException"><paramref name="target"/> 是一个开放式泛型类型。</exception>
		/// <exception cref="System.ArgumentException">无法绑定 <paramref name="memberName"/>。</exception>
		/// <exception cref="System.MissingMethodException">未找到 <typeparamref name="TDelegate"/>
		/// 的 <c>Invoke</c> 方法。</exception>
		/// <exception cref="System.MethodAccessException">调用方无权访问成员。</exception>
		public static TDelegate CreateDelegate<TDelegate>(this Type target, string memberName, object firstArgument)
			where TDelegate : class
		{
			return CreateDelegate(typeof(TDelegate), target, memberName, firstArgument,
				BinderDefault, true) as TDelegate;
		}
		/// <summary>
		/// 使用指定的名称、第一个参数和搜索方式，创建用于表示静态或实例成员的指定类型的委托。
		/// 如果 <paramref name="firstArgument"/> 不为 <c>null</c>，则搜索实例成员，
		/// 并将 <paramref name="firstArgument"/> 作为实例。如果为 <c>null</c>，则搜索静态成员。
		/// 对于属性和字段成员，如果委托具有返回值，则认为是获取属性或字段，否则认为是设置。
		/// 按照方法、属性、字段的顺序查找匹配的成员。
		/// 支持参数的强制类型转换，参数声明可以与实际类型不同。
		/// 可以通过指定 <see cref="BindingFlags.InvokeMethod"/>，<see cref="BindingFlags.CreateInstance"/>，
		/// <see cref="BindingFlags.GetField"/>，<see cref="BindingFlags.SetField"/>，
		/// <see cref="BindingFlags.GetProperty"/>，<see cref="BindingFlags.SetProperty"/> 来选择要绑定到的成员类型。
		/// </summary>
		/// <typeparam name="TDelegate">要创建的委托的类型。</typeparam>
		/// <param name="target">表示实现成员的类的 <see cref="System.Type"/>。</param>
		/// <param name="memberName">委托要表示的成员的名称。</param> 
		/// <param name="firstArgument">委托要绑定到的对象，或为 <c>null</c>，
		/// 后者表示将 <paramref name="memberName"/> 视为 <c>static</c>。</param>
		/// <param name="bindingAttr">一个位屏蔽，由一个或多个指定搜索执行方式的 
		/// <see cref="System.Reflection.BindingFlags"/> 组成。</param>
		/// <returns>指定类型的委托，表示指定的静态或实例成员。</returns>
		/// <exception cref="System.ArgumentNullException"><paramref name="target"/> 为 <c>null</c>。</exception>
		/// <exception cref="System.ArgumentNullException"><paramref name="memberName"/> 为 <c>null</c>。</exception>
		/// <exception cref="System.ArgumentException"><typeparamref name="TDelegate"/> 不继承
		/// <see cref="System.MulticastDelegate"/>。</exception>
		/// <exception cref="System.ArgumentException"><paramref name="target"/> 是一个开放式泛型类型。</exception>
		/// <exception cref="System.ArgumentException">无法绑定 <paramref name="memberName"/>。</exception>
		/// <exception cref="System.MissingMethodException">未找到 <typeparamref name="TDelegate"/>
		/// 的 <c>Invoke</c> 方法。</exception>
		/// <exception cref="System.MethodAccessException">调用方无权访问成员。</exception>
		public static TDelegate CreateDelegate<TDelegate>(this Type target, string memberName,
			object firstArgument, BindingFlags bindingAttr)
			where TDelegate : class
		{
			return CreateDelegate(typeof(TDelegate), target, memberName, firstArgument,
				bindingAttr, true) as TDelegate;
		}
		/// <summary>
		/// 使用指定的名称、第一个参数、搜索方式和针对绑定失败的指定行为，创建用于表示静态或实例成员的指定类型的委托。
		/// 如果 <paramref name="firstArgument"/> 不为 <c>null</c>，则搜索实例成员，
		/// 并将 <paramref name="firstArgument"/> 作为实例。如果为 <c>null</c>，则搜索静态成员。
		/// 对于属性和字段成员，如果委托具有返回值，则认为是获取属性或字段，否则认为是设置。
		/// 按照方法、属性、字段的顺序查找匹配的成员。
		/// 支持参数的强制类型转换，参数声明可以与实际类型不同。
		/// 可以通过指定 <see cref="BindingFlags.InvokeMethod"/>，<see cref="BindingFlags.CreateInstance"/>，
		/// <see cref="BindingFlags.GetField"/>，<see cref="BindingFlags.SetField"/>，
		/// <see cref="BindingFlags.GetProperty"/>，<see cref="BindingFlags.SetProperty"/> 来选择要绑定到的成员类型。
		/// </summary>
		/// <typeparam name="TDelegate">要创建的委托的类型。</typeparam>
		/// <param name="target">表示实现成员的类的 <see cref="System.Type"/>。</param>
		/// <param name="memberName">委托要表示的成员的名称。</param> 
		/// <param name="firstArgument">委托要绑定到的对象，或为 <c>null</c>，
		/// 后者表示将 <paramref name="memberName"/> 视为 <c>static</c>。</param>
		/// <param name="bindingAttr">一个位屏蔽，由一个或多个指定搜索执行方式的 
		/// <see cref="System.Reflection.BindingFlags"/> 组成。</param>
		/// <param name="throwOnBindFailure">为 <c>true</c>，表示无法绑定 <paramref name="memberName"/> 
		/// 时引发异常；否则为 <c>false</c>。</param>
		/// <returns>指定类型的委托，表示指定的静态或实例成员。</returns>
		/// <exception cref="System.ArgumentNullException"><paramref name="target"/> 为 <c>null</c>。</exception>
		/// <exception cref="System.ArgumentNullException"><paramref name="memberName"/> 为 <c>null</c>。</exception>
		/// <exception cref="System.ArgumentException"><typeparamref name="TDelegate"/> 不继承
		/// <see cref="System.MulticastDelegate"/>。</exception>
		/// <exception cref="System.ArgumentException"><paramref name="target"/> 是一个开放式泛型类型。</exception>
		/// <exception cref="System.ArgumentException">无法绑定 <paramref name="memberName"/>
		/// 且 <paramref name="throwOnBindFailure"/> 为 <c>true</c>。</exception>
		/// <exception cref="System.MissingMethodException">未找到 <typeparamref name="TDelegate"/>
		/// 的 <c>Invoke</c> 方法。</exception>
		/// <exception cref="System.MethodAccessException">调用方无权访问成员。</exception>
		public static TDelegate CreateDelegate<TDelegate>(this Type target, string memberName,
			object firstArgument, BindingFlags bindingAttr, bool throwOnBindFailure)
			where TDelegate : class
		{
			return CreateDelegate(typeof(TDelegate), target, memberName, firstArgument,
				bindingAttr, throwOnBindFailure) as TDelegate;
		}
		/// <summary>
		/// 使用指定的名称和第一个参数，创建用于表示静态或实例成员的指定类型的委托。
		/// 如果 <paramref name="firstArgument"/> 不为 <c>null</c>，则搜索实例成员，
		/// 并将 <paramref name="firstArgument"/> 作为实例。如果为 <c>null</c>，则搜索静态成员。
		/// 对于属性和字段成员，如果委托具有返回值，则认为是获取属性或字段，否则认为是设置。
		/// 按照方法、属性、字段的顺序查找匹配的成员。
		/// 支持参数的强制类型转换，参数声明可以与实际类型不同。
		/// 可以通过指定 <see cref="BindingFlags.InvokeMethod"/>，<see cref="BindingFlags.CreateInstance"/>，
		/// <see cref="BindingFlags.GetField"/>，<see cref="BindingFlags.SetField"/>，
		/// <see cref="BindingFlags.GetProperty"/>，<see cref="BindingFlags.SetProperty"/> 来选择要绑定到的成员类型。
		/// </summary>
		/// <param name="type">要创建的委托的类型。</param>
		/// <param name="target">表示实现成员的类的 <see cref="System.Type"/>。</param>
		/// <param name="memberName">委托要表示的成员的名称。</param> 
		/// <param name="firstArgument">委托要绑定到的对象，或为 <c>null</c>，
		/// 后者表示将 <paramref name="memberName"/> 视为 <c>static</c>。</param>
		/// <returns>指定类型的委托，表示指定的静态或实例成员。</returns>
		/// <exception cref="System.ArgumentNullException"><paramref name="type"/> 为 <c>null</c>。</exception>
		/// <exception cref="System.ArgumentNullException"><paramref name="target"/> 为 <c>null</c>。</exception>
		/// <exception cref="System.ArgumentNullException"><paramref name="memberName"/> 为 <c>null</c>。</exception>
		/// <exception cref="System.ArgumentException"><paramref name="type"/> 不继承
		/// <see cref="System.MulticastDelegate"/>。</exception>
		/// <exception cref="System.ArgumentException"><paramref name="target"/> 是一个开放式泛型类型。</exception>
		/// <exception cref="System.ArgumentException">无法绑定 <paramref name="memberName"/>。</exception>
		/// <exception cref="System.MissingMethodException">未找到 <paramref name="type"/>
		/// 的 <c>Invoke</c> 方法。</exception>
		/// <exception cref="System.MethodAccessException">调用方无权访问成员。</exception>
		public static Delegate CreateDelegate(Type type, Type target, string memberName, object firstArgument)
		{
			return CreateDelegate(type, target, memberName, firstArgument, BinderDefault, true);
		}
		/// <summary>
		/// 使用指定的名称、第一个参数和搜索方式，创建用于表示静态或实例成员的指定类型的委托。
		/// 如果 <paramref name="firstArgument"/> 不为 <c>null</c>，则搜索实例成员，
		/// 并将 <paramref name="firstArgument"/> 作为实例。如果为 <c>null</c>，则搜索静态成员。
		/// 对于属性和字段成员，如果委托具有返回值，则认为是获取属性或字段，否则认为是设置。
		/// 按照方法、属性、字段的顺序查找匹配的成员。
		/// 支持参数的强制类型转换，参数声明可以与实际类型不同。
		/// 可以通过指定 <see cref="BindingFlags.InvokeMethod"/>，<see cref="BindingFlags.CreateInstance"/>，
		/// <see cref="BindingFlags.GetField"/>，<see cref="BindingFlags.SetField"/>，
		/// <see cref="BindingFlags.GetProperty"/>，<see cref="BindingFlags.SetProperty"/> 来选择要绑定到的成员类型。
		/// </summary>
		/// <param name="type">要创建的委托的类型。</param>
		/// <param name="target">表示实现成员的类的 <see cref="System.Type"/>。</param>
		/// <param name="memberName">委托要表示的成员的名称。</param> 
		/// <param name="firstArgument">委托要绑定到的对象，或为 <c>null</c>，
		/// 后者表示将 <paramref name="memberName"/> 视为 <c>static</c>。</param>
		/// <param name="bindingAttr">一个位屏蔽，由一个或多个指定搜索执行方式的 
		/// <see cref="System.Reflection.BindingFlags"/> 组成。</param>
		/// <returns>指定类型的委托，表示指定的静态或实例成员。</returns>
		/// <exception cref="System.ArgumentNullException"><paramref name="type"/> 为 <c>null</c>。</exception>
		/// <exception cref="System.ArgumentNullException"><paramref name="target"/> 为 <c>null</c>。</exception>
		/// <exception cref="System.ArgumentNullException"><paramref name="memberName"/> 为 <c>null</c>。</exception>
		/// <exception cref="System.ArgumentException"><paramref name="type"/> 不继承
		/// <see cref="System.MulticastDelegate"/>。</exception>
		/// <exception cref="System.ArgumentException"><paramref name="target"/> 是一个开放式泛型类型。</exception>
		/// <exception cref="System.ArgumentException">无法绑定 <paramref name="memberName"/>。</exception>
		/// <exception cref="System.MissingMethodException">未找到 <paramref name="type"/>
		/// 的 <c>Invoke</c> 方法。</exception>
		/// <exception cref="System.MethodAccessException">调用方无权访问成员。</exception>
		public static Delegate CreateDelegate(Type type, Type target, string memberName, object firstArgument,
			BindingFlags bindingAttr)
		{
			return CreateDelegate(type, target, memberName, firstArgument, bindingAttr, true);
		}
		/// <summary>
		/// 使用指定的名称、第一个参数、搜索方式和针对绑定失败的指定行为，创建用于表示静态或实例成员的指定类型的委托。
		/// 如果 <paramref name="firstArgument"/> 不为 <c>null</c>，则搜索实例成员，
		/// 并将 <paramref name="firstArgument"/> 作为实例。如果为 <c>null</c>，则搜索静态成员。
		/// 对于属性和字段成员，如果委托具有返回值，则认为是获取属性或字段，否则认为是设置。
		/// 按照方法、属性、字段的顺序查找匹配的成员。
		/// 支持参数的强制类型转换，参数声明可以与实际类型不同。
		/// 可以通过指定 <see cref="BindingFlags.InvokeMethod"/>，<see cref="BindingFlags.CreateInstance"/>，
		/// <see cref="BindingFlags.GetField"/>，<see cref="BindingFlags.SetField"/>，
		/// <see cref="BindingFlags.GetProperty"/>，<see cref="BindingFlags.SetProperty"/> 来选择要绑定到的成员类型。
		/// </summary>
		/// <param name="type">要创建的委托的类型。</param>
		/// <param name="target">表示实现成员的类的 <see cref="System.Type"/>。</param>
		/// <param name="memberName">委托要表示的成员的名称。</param> 
		/// <param name="firstArgument">委托要绑定到的对象，或为 <c>null</c>，
		/// 后者表示将 <paramref name="memberName"/> 视为 <c>static</c>。</param>
		/// <param name="bindingAttr">一个位屏蔽，由一个或多个指定搜索执行方式的 
		/// <see cref="System.Reflection.BindingFlags"/> 组成。</param>
		/// <param name="throwOnBindFailure">为 <c>true</c>，表示无法绑定 <paramref name="memberName"/> 
		/// 时引发异常；否则为 <c>false</c>。</param>
		/// <returns>指定类型的委托，表示指定的静态或实例成员。</returns>
		/// <exception cref="System.ArgumentNullException"><paramref name="type"/> 为 <c>null</c>。</exception>
		/// <exception cref="System.ArgumentNullException"><paramref name="target"/> 为 <c>null</c>。</exception>
		/// <exception cref="System.ArgumentNullException"><paramref name="memberName"/> 为 <c>null</c>。</exception>
		/// <exception cref="System.ArgumentException"><paramref name="type"/> 不继承
		/// <see cref="System.MulticastDelegate"/>。</exception>
		/// <exception cref="System.ArgumentException"><paramref name="target"/> 是一个开放式泛型类型。</exception>
		/// <exception cref="System.ArgumentException">无法绑定 <paramref name="memberName"/>
		/// 且 <paramref name="throwOnBindFailure"/> 为 <c>true</c>。</exception>
		/// <exception cref="System.MissingMethodException">未找到 <paramref name="type"/>
		/// 的 <c>Invoke</c> 方法。</exception>
		/// <exception cref="System.MethodAccessException">调用方无权访问成员。</exception>
		public static Delegate CreateDelegate(Type type, Type target, string memberName, object firstArgument,
			BindingFlags bindingAttr, bool throwOnBindFailure)
		{
			CommonExceptions.CheckArgumentNull(memberName, "memberName");
			CommonExceptions.CheckDelegateType(type, "type");
			CheckTargetType(target, "target");
			MethodInfo invoke = type.GetMethod("Invoke");
			ParameterInfo[] paramInfos = invoke.GetParameters();
			Type[] types = GetParameterTypes(paramInfos, 0, 0, 0);
			BindingFlags memberFlags = bindingAttr & BinderMemberMask;
			if (memberFlags == BindingFlags.Default)
			{
				// 未设置 MemberMask 的情况下，查找所有成员。
				memberFlags = BinderMemberMask;
			}
			else
			{
				// 清除 bindingAttr 中的成员设置。
				bindingAttr &= ~BinderMemberMask;
			}
			BindingFlags instancecBindingAttr = (bindingAttr & ~BindingFlags.Static) | BindingFlags.Instance;
			Delegate dlg = null;
			if (memberName.Equals(ConstructorName, StringComparison.OrdinalIgnoreCase))
			{
				// 查找构造函数成员，此时注意只搜索实例方法。
				ConstructorInfo ctor = target.GetConstructor(instancecBindingAttr, PowerBinder.CastBinder, types, null);
				if (ctor != null)
				{
					dlg = CreateDelegate(type, ctor, false);
				}
			}
			else
			{
				// 查找其它成员。
				if (firstArgument == null)
				{
					bindingAttr = (bindingAttr & ~BindingFlags.Instance) | BindingFlags.Static;
				}
				else
				{
					bindingAttr = instancecBindingAttr;
				}
				// 查找方法成员。
				if ((memberFlags & BindingFlags.InvokeMethod) == BindingFlags.InvokeMethod)
				{
					dlg = CreateMethodDelegate(type, target, memberName, firstArgument, bindingAttr, types);
				}
				// 查找属性成员。
				if (dlg == null && (memberFlags & BinderGetSetProperty) != BindingFlags.Default)
				{
					dlg = CreatePropertyDelegate(type, target, memberName, firstArgument, bindingAttr, invoke.ReturnType, types);
				}
				// 查找字段成员。
				if (dlg == null && (memberFlags & BinderGetSetField) != BindingFlags.Default)
				{
					FieldInfo field = target.GetField(memberName, bindingAttr);
					if (field != null)
					{
						if (firstArgument == null)
						{
							dlg = CreateDelegate(type, field, throwOnBindFailure);
						}
						else
						{
							dlg = CreateDelegate(type, field, firstArgument, throwOnBindFailure);
						}
					}
				}
			}
			if (dlg != null)
			{
				return dlg;
			}
			if (throwOnBindFailure)
			{
				throw CommonExceptions.BindTargetMethod("memberName");
			}
			return null;
		}

		#endregion // 从 Type 构造带有第一个参数的成员委托

		#region 从 Object 构造成员委托

		/// <summary>
		/// 使用指定的实例和名称，创建用于表示实例成员的指定类型的委托。
		/// 对于属性和字段成员，如果委托具有返回值，则认为是获取属性或字段，否则认为是设置。
		/// 按照方法、属性、字段的顺序查找匹配的成员。
		/// 支持参数的强制类型转换，参数声明可以与实际类型不同。
		/// 可以通过指定 <see cref="BindingFlags.InvokeMethod"/>，<see cref="BindingFlags.CreateInstance"/>，
		/// <see cref="BindingFlags.GetField"/>，<see cref="BindingFlags.SetField"/>，
		/// <see cref="BindingFlags.GetProperty"/>，<see cref="BindingFlags.SetProperty"/> 来选择要绑定到的成员类型。
		/// </summary>
		/// <typeparam name="TDelegate">要创建的委托的类型。</typeparam>
		/// <param name="target">表示实现成员的类的实例。</param>
		/// <param name="memberName">委托要表示的成员的名称。</param> 
		/// <returns>指定类型的委托，表示指定的实例成员。</returns>
		/// <exception cref="System.ArgumentNullException"><paramref name="target"/> 为 <c>null</c>。</exception>
		/// <exception cref="System.ArgumentNullException"><paramref name="memberName"/> 为 <c>null</c>。</exception>
		/// <exception cref="System.ArgumentException"><typeparamref name="TDelegate"/> 不继承
		/// <see cref="System.MulticastDelegate"/>。</exception>
		/// <exception cref="System.ArgumentException">无法绑定 <paramref name="memberName"/>。</exception>
		/// <exception cref="System.MissingMethodException">未找到 <typeparamref name="TDelegate"/>
		/// 的 <c>Invoke</c> 方法。</exception>
		/// <exception cref="System.MethodAccessException">调用方无权访问成员。</exception>
		public static TDelegate CreateDelegate<TDelegate>(object target, string memberName)
			where TDelegate : class
		{
			CommonExceptions.CheckArgumentNull(target, "target");
			return CreateDelegate(typeof(TDelegate), target.GetType(), memberName, target,
				BinderDefault, true) as TDelegate;
		}
		/// <summary>
		/// 使用指定的实例、名称和搜索方式，创建用于表示实例成员的指定类型的委托。
		/// 对于属性和字段成员，如果委托具有返回值，则认为是获取属性或字段，否则认为是设置。
		/// 按照方法、属性、字段的顺序查找匹配的成员。
		/// 支持参数的强制类型转换，参数声明可以与实际类型不同。
		/// 可以通过指定 <see cref="BindingFlags.InvokeMethod"/>，<see cref="BindingFlags.CreateInstance"/>，
		/// <see cref="BindingFlags.GetField"/>，<see cref="BindingFlags.SetField"/>，
		/// <see cref="BindingFlags.GetProperty"/>，<see cref="BindingFlags.SetProperty"/> 来选择要绑定到的成员类型。
		/// </summary>
		/// <typeparam name="TDelegate">要创建的委托的类型。</typeparam>
		/// <param name="target">表示实现成员的类的实例。</param>
		/// <param name="memberName">委托要表示的成员的名称。</param> 
		/// <param name="bindingAttr">一个位屏蔽，由一个或多个指定搜索执行方式的 
		/// <see cref="System.Reflection.BindingFlags"/> 组成。</param>
		/// <returns>指定类型的委托，表示指定的实例成员。</returns>
		/// <exception cref="System.ArgumentNullException"><paramref name="target"/> 为 <c>null</c>。</exception>
		/// <exception cref="System.ArgumentNullException"><paramref name="memberName"/> 为 <c>null</c>。</exception>
		/// <exception cref="System.ArgumentException"><typeparamref name="TDelegate"/> 不继承
		/// <see cref="System.MulticastDelegate"/>。</exception>
		/// <exception cref="System.ArgumentException">无法绑定 <paramref name="memberName"/>。</exception>
		/// <exception cref="System.MissingMethodException">未找到 <typeparamref name="TDelegate"/>
		/// 的 <c>Invoke</c> 方法。</exception>
		/// <exception cref="System.MethodAccessException">调用方无权访问成员。</exception>
		public static TDelegate CreateDelegate<TDelegate>(object target, string memberName, BindingFlags bindingAttr)
			where TDelegate : class
		{
			CommonExceptions.CheckArgumentNull(target, "target");
			return CreateDelegate(typeof(TDelegate), target.GetType(), memberName, target,
				bindingAttr, true) as TDelegate;
		}
		/// <summary>
		/// 使用指定的实例、名称、搜索方式和针对绑定失败的指定行为，创建用于表示实例成员的指定类型的委托。
		/// 对于属性和字段成员，如果委托具有返回值，则认为是获取属性或字段，否则认为是设置。
		/// 按照方法、属性、字段的顺序查找匹配的成员。
		/// 支持参数的强制类型转换，参数声明可以与实际类型不同。
		/// 可以通过指定 <see cref="BindingFlags.InvokeMethod"/>，<see cref="BindingFlags.CreateInstance"/>，
		/// <see cref="BindingFlags.GetField"/>，<see cref="BindingFlags.SetField"/>，
		/// <see cref="BindingFlags.GetProperty"/>，<see cref="BindingFlags.SetProperty"/> 来选择要绑定到的成员类型。
		/// </summary>
		/// <typeparam name="TDelegate">要创建的委托的类型。</typeparam>
		/// <param name="target">表示实现成员的类的实例。</param>
		/// <param name="memberName">委托要表示的成员的名称。</param> 
		/// <param name="bindingAttr">一个位屏蔽，由一个或多个指定搜索执行方式的 
		/// <see cref="System.Reflection.BindingFlags"/> 组成。</param>
		/// <param name="throwOnBindFailure">为 <c>true</c>，表示无法绑定 <paramref name="memberName"/> 
		/// 时引发异常；否则为 <c>false</c>。</param>
		/// <returns>指定类型的委托，表示指定的实例成员。</returns>
		/// <exception cref="System.ArgumentNullException"><paramref name="target"/> 为 <c>null</c>。</exception>
		/// <exception cref="System.ArgumentNullException"><paramref name="memberName"/> 为 <c>null</c>。</exception>
		/// <exception cref="System.ArgumentException"><typeparamref name="TDelegate"/> 不继承
		/// <see cref="System.MulticastDelegate"/>。</exception>
		/// <exception cref="System.ArgumentException">无法绑定 <paramref name="memberName"/>
		/// 且 <paramref name="throwOnBindFailure"/> 为 <c>true</c>。</exception>
		/// <exception cref="System.MissingMethodException">未找到 <typeparamref name="TDelegate"/>
		/// 的 <c>Invoke</c> 方法。</exception>
		/// <exception cref="System.MethodAccessException">调用方无权访问成员。</exception>
		public static TDelegate CreateDelegate<TDelegate>(object target, string memberName,
			BindingFlags bindingAttr, bool throwOnBindFailure)
			where TDelegate : class
		{
			CommonExceptions.CheckArgumentNull(target, "target");
			return CreateDelegate(typeof(TDelegate), target.GetType(), memberName, target,
				bindingAttr, throwOnBindFailure) as TDelegate;
		}
		/// <summary>
		/// 使用指定的实例和名称创建用于表示实例成员的指定类型的委托。
		/// 对于属性和字段成员，如果委托具有返回值，则认为是获取属性或字段，否则认为是设置。
		/// 按照方法、属性、字段的顺序查找匹配的成员。
		/// 支持参数的强制类型转换，参数声明可以与实际类型不同。
		/// 可以通过指定 <see cref="BindingFlags.InvokeMethod"/>，<see cref="BindingFlags.CreateInstance"/>，
		/// <see cref="BindingFlags.GetField"/>，<see cref="BindingFlags.SetField"/>，
		/// <see cref="BindingFlags.GetProperty"/>，<see cref="BindingFlags.SetProperty"/> 来选择要绑定到的成员类型。
		/// </summary>
		/// <param name="type">要创建的委托的类型。</param>
		/// <param name="target">表示实现成员的类的实例。</param>
		/// <param name="memberName">委托要表示的成员的名称。</param> 
		/// <returns>指定类型的委托，表示指定的实例成员。</returns>
		/// <exception cref="System.ArgumentNullException"><paramref name="type"/> 为 <c>null</c>。</exception>
		/// <exception cref="System.ArgumentNullException"><paramref name="target"/> 为 <c>null</c>。</exception>
		/// <exception cref="System.ArgumentNullException"><paramref name="memberName"/> 为 <c>null</c>。</exception>
		/// <exception cref="System.ArgumentException"><paramref name="type"/> 不继承
		/// <see cref="System.MulticastDelegate"/>。</exception>
		/// <exception cref="System.ArgumentException">无法绑定 <paramref name="memberName"/>。</exception>
		/// <exception cref="System.MissingMethodException">未找到 <paramref name="type"/>
		/// 的 <c>Invoke</c> 方法。</exception>
		/// <exception cref="System.MethodAccessException">调用方无权访问成员。</exception>
		public static Delegate CreateDelegate(Type type, object target, string memberName)
		{
			CommonExceptions.CheckArgumentNull(target, "target");
			return CreateDelegate(type, target.GetType(), memberName, target, BinderDefault, true);
		}
		/// <summary>
		/// 使用指定的实例、名称和搜索方式，创建用于表示实例成员的指定类型的委托。
		/// 对于属性和字段成员，如果委托具有返回值，则认为是获取属性或字段，否则认为是设置。
		/// 按照方法、属性、字段的顺序查找匹配的成员。
		/// 支持参数的强制类型转换，参数声明可以与实际类型不同。
		/// 可以通过指定 <see cref="BindingFlags.InvokeMethod"/>，<see cref="BindingFlags.CreateInstance"/>，
		/// <see cref="BindingFlags.GetField"/>，<see cref="BindingFlags.SetField"/>，
		/// <see cref="BindingFlags.GetProperty"/>，<see cref="BindingFlags.SetProperty"/> 来选择要绑定到的成员类型。
		/// </summary>
		/// <param name="type">要创建的委托的类型。</param>
		/// <param name="target">表示实现成员的类的实例。</param>
		/// <param name="memberName">委托要表示的成员的名称。</param> 
		/// <param name="bindingAttr">一个位屏蔽，由一个或多个指定搜索执行方式的 
		/// <see cref="System.Reflection.BindingFlags"/> 组成。</param>
		/// <returns>指定类型的委托，表示指定的实例成员。</returns>
		/// <exception cref="System.ArgumentNullException"><paramref name="type"/> 为 <c>null</c>。</exception>
		/// <exception cref="System.ArgumentNullException"><paramref name="target"/> 为 <c>null</c>。</exception>
		/// <exception cref="System.ArgumentNullException"><paramref name="memberName"/> 为 <c>null</c>。</exception>
		/// <exception cref="System.ArgumentException"><paramref name="type"/> 不继承
		/// <see cref="System.MulticastDelegate"/>。</exception>
		/// <exception cref="System.ArgumentException">无法绑定 <paramref name="memberName"/>。</exception>
		/// <exception cref="System.MissingMethodException">未找到 <paramref name="type"/>
		/// 的 <c>Invoke</c> 方法。</exception>
		/// <exception cref="System.MethodAccessException">调用方无权访问成员。</exception>
		public static Delegate CreateDelegate(Type type, object target, string memberName, BindingFlags bindingAttr)
		{
			CommonExceptions.CheckArgumentNull(target, "target");
			return CreateDelegate(type, target.GetType(), memberName, target, bindingAttr, true);
		}
		/// <summary>
		/// 使用指定的实例、名称、搜索方式和针对绑定失败的指定行为，创建用于表示实例成员的指定类型的委托。
		/// 对于属性和字段成员，如果委托具有返回值，则认为是获取属性或字段，否则认为是设置。
		/// 按照方法、属性、字段的顺序查找匹配的成员。
		/// 支持参数的强制类型转换，参数声明可以与实际类型不同。
		/// 可以通过指定 <see cref="BindingFlags.InvokeMethod"/>，<see cref="BindingFlags.CreateInstance"/>，
		/// <see cref="BindingFlags.GetField"/>，<see cref="BindingFlags.SetField"/>，
		/// <see cref="BindingFlags.GetProperty"/>，<see cref="BindingFlags.SetProperty"/> 来选择要绑定到的成员类型。
		/// </summary>
		/// <param name="type">要创建的委托的类型。</param>
		/// <param name="target">表示实现成员的类的实例。</param>
		/// <param name="memberName">委托要表示的成员的名称。</param> 
		/// <param name="bindingAttr">一个位屏蔽，由一个或多个指定搜索执行方式的 
		/// <see cref="System.Reflection.BindingFlags"/> 组成。</param>
		/// <param name="throwOnBindFailure">为 <c>true</c>，表示无法绑定 <paramref name="memberName"/> 
		/// 时引发异常；否则为 <c>false</c>。</param>
		/// <returns>指定类型的委托，表示指定的实例成员。</returns>
		/// <exception cref="System.ArgumentNullException"><paramref name="type"/> 为 <c>null</c>。</exception>
		/// <exception cref="System.ArgumentNullException"><paramref name="target"/> 为 <c>null</c>。</exception>
		/// <exception cref="System.ArgumentNullException"><paramref name="memberName"/> 为 <c>null</c>。</exception>
		/// <exception cref="System.ArgumentException"><paramref name="type"/> 不继承
		/// <see cref="System.MulticastDelegate"/>。</exception>
		/// <exception cref="System.ArgumentException">无法绑定 <paramref name="memberName"/>
		/// 且 <paramref name="throwOnBindFailure"/> 为 <c>true</c>。</exception>
		/// <exception cref="System.MissingMethodException">未找到 <paramref name="type"/>
		/// 的 <c>Invoke</c> 方法。</exception>
		/// <exception cref="System.MethodAccessException">调用方无权访问成员。</exception>
		public static Delegate CreateDelegate(Type type, object target, string memberName,
			BindingFlags bindingAttr, bool throwOnBindFailure)
		{
			CommonExceptions.CheckArgumentNull(target, "target");
			return CreateDelegate(type, target.GetType(), memberName, target, bindingAttr, throwOnBindFailure);
		}

		#endregion // 从 Object 构造成员委托

		#region 委托类型包装

		/// <summary>
		/// 将指定的委托用指定类型的委托包装，支持对参数进行强制类型转换。
		/// </summary>
		/// <typeparam name="TDelegate">要创建的委托的类型。</typeparam>
		/// <param name="dlg">要被包装的委托。</param>
		/// <returns>指定类型的委托，其包装了 <paramref name="dlg"/>。
		/// 如果参数个数不同，或者参数间不能执行强制类型转换，则为 <c>null</c>。</returns>
		/// <exception cref="System.ArgumentNullException"><paramref name="dlg"/> 为 <c>null</c>。</exception>
		/// <exception cref="System.ArgumentException"><typeparamref name="TDelegate"/> 不继承
		/// <see cref="System.MulticastDelegate"/>。</exception>
		/// <exception cref="System.MissingMethodException">未找到 <typeparamref name="TDelegate"/>
		/// 的 <c>Invoke</c> 方法。</exception>
		/// <exception cref="System.MethodAccessException">调用方无权访问成员。</exception>
		/// <overloads>
		/// <summary>
		/// 将指定的委托用指定类型的委托包装，支持对参数进行强制类型转换。
		/// </summary>
		/// </overloads>
		public static TDelegate Wrap<TDelegate>(this Delegate dlg)
			where TDelegate : class
		{
			// TODO : 完全兼容的话不要创建新：的
			return Wrap(typeof(TDelegate), dlg) as TDelegate;
		}
		/// <summary>
		/// 将指定的委托用指定类型的委托包装，支持对参数进行强制类型转换。
		/// </summary>
		/// <param name="type">要创建的委托的类型。</param>
		/// <param name="dlg">要被包装的委托。</param>
		/// <returns>指定类型的委托，其包装了 <paramref name="dlg"/>。
		/// 如果参数个数不同，或者参数间不能执行强制类型转换，则为 <c>null</c>。</returns>
		/// <exception cref="System.ArgumentNullException"><paramref name="type"/> 为 <c>null</c>。</exception>
		/// <exception cref="System.ArgumentNullException"><paramref name="dlg"/> 为 <c>null</c>。</exception>
		/// <exception cref="System.ArgumentException"><paramref name="type"/> 不继承
		/// <see cref="System.MulticastDelegate"/>。</exception>
		/// <exception cref="System.MethodAccessException">调用方无权访问成员。</exception>
		public static Delegate Wrap(Type type, Delegate dlg)
		{
			CommonExceptions.CheckArgumentNull(dlg, "dlg");
			CommonExceptions.CheckDelegateType(type, "type");
			ParameterInfo[] paramInfos = dlg.GetType().GetMethod("Invoke").GetParameters();
			MethodInfo targetInvoke = type.GetMethod("Invoke");
			ParameterInfo[] targetParamInfos = targetInvoke.GetParameters();
			if (paramInfos.Length != targetParamInfos.Length)
			{
				return null;
			}
			ParameterExpression[] paramList = targetParamInfos.ToExpressions();
			// 构造调用参数列表。
			Expression[] paramExps = GetParameterExpressions(paramList, 0, paramInfos, 0);
			if (paramExps == null)
			{
				return null;
			}
			Expression dlgInvoke = GetReturn(Expression.Invoke(Expression.Constant(dlg), paramExps),
				targetInvoke.ReturnType);
			if (dlgInvoke == null)
			{
				return null;
			}
			return Expression.Lambda(type, dlgInvoke, paramList).Compile();
		}

		#endregion // 委托类型包装

		#region 参数检查

		/// <summary>
		/// 检查目标类型是否合法。
		/// </summary>
		/// <param name="target">目标的类型。</param>
		/// <param name="paramName">参数的名称。</param>
		private static void CheckTargetType(Type target, string paramName)
		{
			CommonExceptions.CheckArgumentNull(target, paramName);
			if (target.IsGenericType && target.ContainsGenericParameters)
			{
				throw CommonExceptions.UnboundGenParam(paramName);
			}
		}

		#endregion // 参数检查

		#region Expression 辅助方法

		/// <summary>
		/// 返回检查参数数组长度的表达式。认为 count 大于 0。
		/// </summary>
		/// <param name="paramExp">要检查长度的参数数组的表达式。</param>
		/// <param name="count">预期的参数数目。</param>
		/// <returns>检查参数数组长度的表达式。</returns>
		private static Expression GetCheckParameterExp(Expression paramExp, int count)
		{
			return Expression.IfThen(
				Expression.OrElse(
				// paramExp == null
					Expression.Equal(paramExp, Expression.Constant(null)),
				// paramExp.Length != count
					Expression.NotEqual(Expression.PropertyOrField(paramExp, "Length"), Expression.Constant(count))),
				Expression.Throw(Expression.New(typeof(TargetParameterCountException))));
		}
		/// <summary>
		/// 返回给定的参数信息的参数类型列表。
		/// </summary>
		/// <param name="paramInfos">参数信息。</param>
		/// <param name="sourceIndex">要获取参数类型的起始索引。</param>
		/// <param name="destinationIndex">要保存到的参数类型列表的起始索引。</param>
		/// <param name="extLen">参数类型列表的额外长度。</param>
		/// <returns>参数类型列表。</returns>
		private static Type[] GetParameterTypes(ParameterInfo[] paramInfos,
			int sourceIndex, int destinationIndex, int extLen)
		{
			int dif = sourceIndex - destinationIndex;
			Type[] types = new Type[paramInfos.Length - dif + extLen];
			for (int i = sourceIndex; i < paramInfos.Length; i++)
			{
				types[i - dif] = paramInfos[i].ParameterType;
			}
			return types;
		}
		/// <summary>
		/// 根据给定的参数信息创建引用参数表达式列表。如果不能进行强制类型转换，则为 <c>null</c>。
		/// </summary>
		/// <param name="parameters">参数表达式列表。</param>
		/// <param name="invokeIndex">参数表达式列表的起始索引。</param>
		/// <param name="paramInfos">目标参数信息。</param>
		/// <param name="methodIndex">目标参数信息的起始索引。</param>
		/// <returns>得到的引用参数表达式列表。</returns>
		private static Expression[] GetParameterExpressions(ParameterExpression[] parameters,
			int invokeIndex, ParameterInfo[] paramInfos, int methodIndex)
		{
			Expression[] paramExps = new Expression[paramInfos.Length];
			for (int i = invokeIndex, j = methodIndex; j < paramExps.Length; i++, j++)
			{
				Expression exp = parameters[i].ConvertType(paramInfos[j].ParameterType);
				if (exp == null)
				{
					// 不能进行强制类型转换。
					return null;
				}
				paramExps[j] = exp;
			}
			return paramExps;
		}
		/// <summary>
		/// 返回对指定返回值到目标类型的强制类型转换的表达式。
		/// </summary>
		/// <param name="returnExp">返回值定义表达式。</param>
		/// <param name="returnType">要强制类型转换到的目标类型。</param>
		/// <returns>指定返回值到目标类型的强制类型转换的表达式。</returns>
		private static Expression GetReturn(Expression returnExp, Type returnType)
		{
			if (returnType == typeof(void) || returnExp.Type == returnType ||
				// Expression 不能处理值类型的类型转换。
				(!returnType.IsValueType && !returnExp.Type.IsValueType &&
				returnType.IsAssignableFrom(returnExp.Type)))
			{
				return returnExp;
			}
			if (returnExp.Type == typeof(void))
			{
				// 添加默认返回值。
				return Expression.Block(returnExp, Expression.Default(returnType));
			}
			try
			{
				// 需要强制转换。
				return Expression.Convert(returnExp, returnType);
			}
			catch (InvalidOperationException)
			{
				// 不允许进行强制类型转换。
				return null;
			}
		}

		#endregion // Expression 辅助方法

	}
}