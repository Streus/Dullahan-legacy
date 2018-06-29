﻿using System;

namespace Dullahan
{
	[AttributeUsage(AttributeTargets.Delegate | AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
	public class CommandAttribute : Attribute
	{
		public string Invocation { get; set; }
		public string Help { get; set; }
	}

	/// <summary>
	/// The signature required for command-marked methods
	/// </summary>
	/// <param name="args"></param>
	/// <returns></returns>
	public delegate int CommandDelegate(string[] args); 
}
