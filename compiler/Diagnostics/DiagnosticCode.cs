namespace Nebra.Diagnostics;

/// <summary>
/// The actual diagnotics of the compiler. Each diagnostic has a code that is used to identify the specific diagnostic.
/// This code is used to provide more information about the diagnostic, such as a description of the issue and potential fixes.
/// This enum should set specific enum values instead of relying on the default values, as the values are used to identify the specific diagnostic and should not change.
/// </summary>
public enum DiagnosticCode
{
    #region Internal

    [Level(DiagnosticLevel.Error)]
    [Category(DiagnosticCategory.Internal)]
    [Format("Preparsing did not return a valid HIR file")]
    ErrPreparsingFailed = -0x0001,
    
    [Level(DiagnosticLevel.Error)]
    [Category(DiagnosticCategory.Internal)]
    [Format("Declaring symbol {0} in non-existing scope {1} is not allowed")]
    ErrDeclaringInNonExistingScope = -0x0002,
    
    [Level(DiagnosticLevel.Error)]
    [Category(DiagnosticCategory.Internal)]
    [Format("Declaring non-existing symbol {0} ({1}) is not allowed")]
    ErrDeclaringNonExistingSymbol = -0x0003,
    
    [Level(DiagnosticLevel.Error)]
    [Category(DiagnosticCategory.Internal)]
    [Format("Looking up symbol {0} in non-existing scope {1} is not allowed")]
    ErrLookingUpInNonExistingScope = -0x0004,

    #endregion

    #region Syntax

    [Level(DiagnosticLevel.Error)]
    [Category(DiagnosticCategory.Syntax)]
    [Format("unexpected end of file")]
    [Help("a construct was left unfinished - check for a missing 'end', ')' or '}}'")]
    ErrUnexpectedEOF = 0x0001,

    [Level(DiagnosticLevel.Error)]
    [Category(DiagnosticCategory.Syntax)]
    [Format("{0}")]
    ErrUnexpectedToken = 0x0002,
    
    [Level(DiagnosticLevel.Error)]
    [Category(DiagnosticCategory.Syntax)]
    [Format("Undefined token at lexing: {0}")]
    ErrLexerUndefinedToken = 0x0005,
    
    [Level(DiagnosticLevel.Error)]
    [Category(DiagnosticCategory.Syntax)]
    [Format("Invalid operator '{0}'")]
    ErrInvalidOperator = 0x0003,
    
    [Level(DiagnosticLevel.Error)]
    [Category(DiagnosticCategory.Syntax)]
    [Format("Invalid literal '{0}', expected {1}")]
    ErrInvalidLiteral = 0x0004,

    #endregion
    
    #region Semantic
    
    [Level(DiagnosticLevel.Error)]
    [Category(DiagnosticCategory.Semantic)]
    [Format("Symbol '{0}' is already declared in this scope ({1})")]
    ErrRedeclaration = 0x1001,
    
    [Level(DiagnosticLevel.Error)]
    [Category(DiagnosticCategory.Semantic)]
    [Format("cannot find '{0}' in this scope")]
    [Help("declare '{0}', import it from another module, or check for a typo")]
    ErrUndeclaredSymbol = 0x1002,
    
    [Level(DiagnosticLevel.Error)]
    [Category(DiagnosticCategory.Semantic)]
    [Format("Top-level cycles are not allowed, but '{0}' is part of a cycle")]
    ErrTopLevelCycle = 0x1003,
    
    [Level(DiagnosticLevel.Error)]
    [Category(DiagnosticCategory.Semantic)]
    [Format("Control flow depth invalid for {0}; it must be a non-negative (0 excluded) integer")]
    ErrInvalidControlFlowDepth = 0x1004,
    
    [Level(DiagnosticLevel.Warning)]
    [Category(DiagnosticCategory.Semantic)]
    [Format("Code is unreachable and will never be executed")]
    WrnUnreachableCode = 0x1005,

    [Level(DiagnosticLevel.Error)]
    [Category(DiagnosticCategory.Semantic)]
    [Format("String interpolation is disabled in the configuration. Enable [code] string_interpolation = true to use backtick strings.")]
    ErrStringInterpolationDisabled = 0x1006,

    [Level(DiagnosticLevel.Error)]
    [Category(DiagnosticCategory.Semantic)]
    [Format("Code is unreachable: the preceding call to '{0}' returns 'never'")]
    [Help("remove the dead code, or move it before the diverging call")]
    ErrUnreachableAfterNever = 0x1007,

    [Level(DiagnosticLevel.Error)]
    [Category(DiagnosticCategory.Semantic)]
    [Format("'break {0}' needs 'goto', which the '{1}' target does not have")]
    [Help("restructure the loops, or raise the target to a Lua version with 'goto'")]
    ErrMultiLevelBreakUnsupported = 0x1008,

    [Level(DiagnosticLevel.Error)]
    [Category(DiagnosticCategory.Semantic)]
    [Format("Bitwise operators are not available on the '{0}' target")]
    [Help("raise the target to a Lua version with bitwise operators, or use the LuaJIT target where they lower to the 'bit' library")]
    ErrBitwiseUnsupported = 0x1009,

    [Level(DiagnosticLevel.Error)]
    [Category(DiagnosticCategory.Syntax)]
    [Format("Alternative boolean operator '{0}' is disabled in the configuration. Enable [code] alt_boolean_operators = true to use it, or use '{1}' instead.")]
    ErrAltBooleanOperatorsDisabled = 0x0006,

    #endregion

    #region Type
    
    [Level(DiagnosticLevel.Error)]
    [Category(DiagnosticCategory.Type)]
    [Format("expected '{0}', but got '{1}'")]
    [Help("change the value to a '{0}', adjust the annotation, or use 'as {0}' to assert the type")]
    ErrTypeMismatch = 0x2001,
    
    [Level(DiagnosticLevel.Error)]
    [Category(DiagnosticCategory.Type)]
    [Format("type '{0}' cannot be indexed")]
    ErrTypeNotIndexable = 0x2002,

    [Level(DiagnosticLevel.Error)]
    [Category(DiagnosticCategory.Type)]
    [Format("type '{0}' has no method '{1}'")]
    [Help("check the method name for a typo, or add it to '{0}' with an 'extend {0} ... end' block")]
    ErrNoSuchMethod = 0x2011,
    
    [Level(DiagnosticLevel.Error)]
    [Category(DiagnosticCategory.Type)]
    [Format("Unknown type '{0}'")]
    ErrUnknownType = 0x2003,
    
    [Level(DiagnosticLevel.Error)]
    [Category(DiagnosticCategory.Type)]
    [Format("this function takes {0} argument(s), but {1} were supplied")]
    [Help("pass {0} argument(s), or make trailing parameters optional with '?' or a default value")]
    ErrFuncParamMismatch = 0x2004,
    
    [Level(DiagnosticLevel.Error)]
    [Category(DiagnosticCategory.Type)]
    [Format("Type inference failed for expression of type '{0}'")]
    ErrTypeInferenceFailed = 0x2005,

    [Level(DiagnosticLevel.Error)]
    [Category(DiagnosticCategory.Type)]
    [Format("Expression of type '{0}' is possibly nil. Use '?.' to access fields safely or check for nil first.")]
    ErrPossiblyNil = 0x2006,

    [Level(DiagnosticLevel.Error)]
    [Category(DiagnosticCategory.Type)]
    [Format("Non-exhaustive match on type '{0}': missing case(s) for {1}. Handle the missing case(s) explicitly.")]
    ErrNonExhaustiveMatch = 0x2007,

    [Level(DiagnosticLevel.Error)]
    [Category(DiagnosticCategory.Type)]
    [Format("Invalid assignment target. Only variables, fields, and index expressions can be assigned to.")]
    ErrInvalidAssignTarget = 0x2008,

    [Level(DiagnosticLevel.Error)]
    [Category(DiagnosticCategory.Semantic)]
    [Format("Cannot assign to immutable variable '{0}'")]
    ErrAssignToImmutable = 0x2009,

    [Level(DiagnosticLevel.Error)]
    [Category(DiagnosticCategory.Semantic)]
    [Format("Cannot modify field of frozen table '{0}'")]
    ErrModifyFrozenTable = 0x200A,

    [Level(DiagnosticLevel.Error)]
    [Category(DiagnosticCategory.Type)]
    [Format("'await' can only be used on function calls")]
    ErrAwaitNonCallable = 0x200B,

    [Level(DiagnosticLevel.Error)]
    [Category(DiagnosticCategory.Type)]
    [Format("'await' used on function '{0}' which has no callback parameter and is not async")]
    ErrAwaitNonAsync = 0x200C,

    [Level(DiagnosticLevel.Error)]
    [Category(DiagnosticCategory.Semantic)]
    [Format("'await' can only be used inside an 'async' function")]
    ErrAwaitOutsideAsync = 0x200D,

    [Level(DiagnosticLevel.Error)]
    [Category(DiagnosticCategory.Type)]
    [Format("not all code paths return a '{0}'")]
    [Help("return a value from every branch, or make the return type nilable ('{0}?') or 'void'")]
    ErrMissingReturn = 0x200E,

    [Level(DiagnosticLevel.Error)]
    [Category(DiagnosticCategory.Type)]
    [Format("Extension method '{0}' is already defined on type '{1}'")]
    ErrDuplicateExtension = 0x200F,

    [Level(DiagnosticLevel.Error)]
    [Category(DiagnosticCategory.Type)]
    [Format("Type predicate refers to '{0}', which is not a parameter of this function")]
    ErrUnknownPredicateParam = 0x2010,

    #endregion

    #region Module

    [Level(DiagnosticLevel.Error)]
    [Category(DiagnosticCategory.Semantic)]
    [Format("Module '{0}' could not be found")]
    ErrModuleNotFound = 0x3001,

    [Level(DiagnosticLevel.Error)]
    [Category(DiagnosticCategory.Semantic)]
    [Format("Symbol '{0}' is not exported from module '{1}'")]
    ErrSymbolNotExported = 0x3002,

    [Level(DiagnosticLevel.Error)]
    [Category(DiagnosticCategory.Semantic)]
    [Format("Symbol '{0}' does not exist in module '{1}'")]
    ErrSymbolNotFound = 0x3003,

    #endregion

    #region Class

    [Level(DiagnosticLevel.Error)]
    [Category(DiagnosticCategory.Type)]
    [Format("Class '{0}' does not implement interface member '{1}' from '{2}'")]
    ErrMissingInterfaceMember = 0x4001,

    [Level(DiagnosticLevel.Error)]
    [Category(DiagnosticCategory.Semantic)]
    [Format("'super()' can only be used inside a constructor of a class that extends another class")]
    ErrSuperOutsideConstructor = 0x4002,

    [Level(DiagnosticLevel.Error)]
    [Category(DiagnosticCategory.Semantic)]
    [Format("'new' can only be used with class types, but '{0}' is not a class")]
    ErrNewNonClass = 0x4003,

    [Level(DiagnosticLevel.Error)]
    [Category(DiagnosticCategory.Semantic)]
    [Format("Class '{0}' does not have a constructor")]
    ErrNoConstructor = 0x4004,

    [Level(DiagnosticLevel.Error)]
    [Category(DiagnosticCategory.Semantic)]
    [Format("Cannot extend '{0}': it is not a class")]
    ErrExtendsNonClass = 0x4005,

    [Level(DiagnosticLevel.Error)]
    [Category(DiagnosticCategory.Semantic)]
    [Format("Cannot implement '{0}': it is not an interface")]
    ErrImplementsNonInterface = 0x4006,

    [Level(DiagnosticLevel.Error)]
    [Category(DiagnosticCategory.Semantic)]
    [Format("Accessor must be 'get' or 'set', found '{0}'")]
    ErrInvalidAccessor = 0x4007,

    [Level(DiagnosticLevel.Error)]
    [Category(DiagnosticCategory.Semantic)]
    [Format("Property '{0}' is read-only")]
    ErrWriteToReadonly = 0x4008,

    [Level(DiagnosticLevel.Error)]
    [Category(DiagnosticCategory.Semantic)]
    [Format("Duplicate class member '{0}'")]
    ErrDuplicateClassMember = 0x4009,

    [Level(DiagnosticLevel.Error)]
    [Category(DiagnosticCategory.Type)]
    [Format("Constructor parameter count mismatch for class '{0}': expected {1}, but got {2}")]
    ErrConstructorParamMismatch = 0x400A,

    [Level(DiagnosticLevel.Error)]
    [Category(DiagnosticCategory.Semantic)]
    [Format("'super()' must be the first statement in a derived class constructor")]
    ErrSuperNotFirst = 0x400B,

    [Level(DiagnosticLevel.Error)]
    [Category(DiagnosticCategory.Semantic)]
    [Format("Derived class '{0}' constructor must call 'super()'")]
    ErrMissingSuperCall = 0x400C,

    [Level(DiagnosticLevel.Error)]
    [Category(DiagnosticCategory.Semantic)]
    [Format("Cannot instantiate abstract class '{0}'")]
    ErrInstantiateAbstract = 0x400D,

    [Level(DiagnosticLevel.Error)]
    [Category(DiagnosticCategory.Semantic)]
    [Format("Non-abstract class '{0}' must implement abstract method '{1}' from '{2}'")]
    ErrMissingAbstractMember = 0x400E,

    [Level(DiagnosticLevel.Error)]
    [Category(DiagnosticCategory.Semantic)]
    [Format("Abstract method '{0}' can only be declared in an abstract class")]
    ErrAbstractInNonAbstractClass = 0x400F,

    [Level(DiagnosticLevel.Error)]
    [Category(DiagnosticCategory.Semantic)]
    [Format("Method '{0}' is marked 'override' but no matching method exists in parent class")]
    ErrOverrideNoParent = 0x4010,

    [Level(DiagnosticLevel.Error)]
    [Category(DiagnosticCategory.Semantic)]
    [Format("Cannot access protected member '{0}' of class '{1}' from outside the class hierarchy")]
    ErrProtectedAccess = 0x4011,

    [Level(DiagnosticLevel.Warning)]
    [Category(DiagnosticCategory.Semantic)]
    [Format("Method '{0}' shadows a method in parent class '{1}'; use 'override' to indicate this is intentional")]
    WarnMissingShadowOverride = 0x4012,

    [Level(DiagnosticLevel.Error)]
    [Category(DiagnosticCategory.Type)]
    [Format("Method '{0}' on class '{1}' takes {2} parameter(s), but interface '{3}' declares {4}")]
    [Help("give the method the parameters '{3}' declares, or make the extra ones optional with '?' or a default value")]
    ErrInterfaceMethodArity = 0x4013,

    [Level(DiagnosticLevel.Error)]
    [Category(DiagnosticCategory.Type)]
    [Format("Parameter '{0}' of method '{1}' on class '{2}' is '{3}', but interface '{4}' declares '{5}'")]
    [Help("a call routed through '{4}' passes a '{5}', so the parameter has to accept one")]
    ErrInterfaceMethodParamType = 0x4014,

    [Level(DiagnosticLevel.Error)]
    [Category(DiagnosticCategory.Type)]
    [Format("Method '{0}' on class '{1}' returns '{2}', but interface '{3}' declares '{4}'")]
    [Help("a call routed through '{3}' expects a '{4}', so the return type has to fit into one")]
    ErrInterfaceMethodReturnType = 0x4015,

    [Level(DiagnosticLevel.Error)]
    [Category(DiagnosticCategory.Type)]
    [Format("Method '{0}' on class '{1}' takes no variadic tail, but interface '{2}' declares one")]
    [Help("add a '...' parameter so arguments passed through '{2}' are not dropped")]
    ErrInterfaceMethodVararg = 0x4016,

    [Level(DiagnosticLevel.Error)]
    [Category(DiagnosticCategory.Type)]
    [Format("Field '{0}' on class '{1}' is '{2}', but interface '{3}' declares '{4}'")]
    [Help("give the field the type '{3}' declares, since both reads and writes go through it")]
    ErrInterfaceFieldType = 0x4017,

    [Level(DiagnosticLevel.Error)]
    [Category(DiagnosticCategory.Semantic)]
    [Format("Parameter '{0}' of method '{1}' on class '{2}' must declare its own default value")]
    [Help("the default interface '{3}' declares is not a literal, so it cannot be inherited into this class; write the default on this parameter")]
    ErrInterfaceDefaultNotInheritable = 0x4018,

    [Level(DiagnosticLevel.Error)]
    [Category(DiagnosticCategory.Semantic)]
    [Format("'{0}' cannot extend '{1}': that would make it its own ancestor")]
    [Help("break the cycle so the chain of base types ends")]
    ErrCyclicInheritance = 0x4019,

    [Level(DiagnosticLevel.Error)]
    [Category(DiagnosticCategory.Type)]
    [Format("Primitive type '{0}' cannot take generic type arguments")]
    ErrGenericOnPrimitive = 0x4020,

    [Level(DiagnosticLevel.Error)]
    [Category(DiagnosticCategory.Type)]
    [Format("Type '{0}' expects {1} type argument(s), but got {2}")]
    ErrTypeParamArityMismatch = 0x4021,

    [Level(DiagnosticLevel.Error)]
    [Category(DiagnosticCategory.Type)]
    [Format("Type argument '{0}' does not satisfy the constraint '{1}' of type parameter '{2}'")]
    ErrTypeParamBoundViolation = 0x4022,

    [Level(DiagnosticLevel.Error)]
    [Category(DiagnosticCategory.Semantic)]
    [Format("Type parameter '{0}' is not in scope")]
    ErrTypeParamOutOfScope = 0x4023,

    [Level(DiagnosticLevel.Error)]
    [Category(DiagnosticCategory.Type)]
    [Format("Wildcard type arguments are only allowed inside generic type references")]
    ErrWildcardNotAllowedHere = 0x4024,

    [Level(DiagnosticLevel.Warning)]
    [Category(DiagnosticCategory.Type)]
    [Format("Generic type arguments on 'instanceof' are erased at runtime; only the raw type '{0}' is checked")]
    WarnGenericInstanceOfErased = 0x4025,

    [Level(DiagnosticLevel.Error)]
    [Category(DiagnosticCategory.Type)]
    [Format("'instanceof' requires a class or interface type, but got '{0}'")]
    ErrInstanceOfNonClass = 0x4026,

    [Level(DiagnosticLevel.Error)]
    [Category(DiagnosticCategory.Type)]
    [Format("Type '{0}' is not generic and cannot take type arguments")]
    ErrNonGenericTypeArgs = 0x4027,

    [Level(DiagnosticLevel.Error)]
    [Category(DiagnosticCategory.Semantic)]
    [Format("Duplicate type parameter '{0}'")]
    ErrDuplicateTypeParam = 0x4028,

    [Level(DiagnosticLevel.Error)]
    [Category(DiagnosticCategory.Semantic)]
    [Format("Instance method '{0}' on class '{1}' must be called with ':' (try `{2}:{0}(...)`); '.' would not pass the receiver as self")]
    ErrInstanceMethodNeedsColon = 0x4029,

    [Level(DiagnosticLevel.Error)]
    [Category(DiagnosticCategory.Type)]
    [Format("A function returning 'never' must not be able to complete normally")]
    [Help("end every path with a call that returns 'never' (such as `error(...)`), or with an endless loop")]
    ErrNeverFunctionCompletes = 0x402A,

    [Level(DiagnosticLevel.Error)]
    [Category(DiagnosticCategory.Type)]
    [Format("A function returning 'never' cannot return a value")]
    ErrNeverFunctionReturnsValue = 0x402B,

    [Level(DiagnosticLevel.Warning)]
    [Category(DiagnosticCategory.Semantic)]
    [Format("'{0}' is called with ':' but declares no 'self' parameter; the receiver is passed as its first argument")]
    [Help("call it with '.' instead, or give it an explicit 'self' first parameter")]
    WarnColonCallWithoutSelf = 0x402C,

    #endregion

    #region Annotations

    [Level(DiagnosticLevel.Error)]
    [Category(DiagnosticCategory.Semantic)]
    [Format("Annotation path '{0}' does not exist")]
    ErrAnnotationPathNotFound = 0x5001,

    [Level(DiagnosticLevel.Error)]
    [Category(DiagnosticCategory.Semantic)]
    [Format("Failed to compile annotation definition '{0}'")]
    ErrAnnotationCompileFailed = 0x5002,

    [Level(DiagnosticLevel.Error)]
    [Category(DiagnosticCategory.Semantic)]
    [Format("Annotation '{0}' has invalid meta: {1}")]
    ErrAnnotationMetaInvalid = 0x5003,

    [Level(DiagnosticLevel.Error)]
    [Category(DiagnosticCategory.Semantic)]
    [Format("Annotation '{0}' is missing an exported `apply` function")]
    ErrAnnotationMissingApply = 0x5004,

    [Level(DiagnosticLevel.Error)]
    [Category(DiagnosticCategory.Semantic)]
    [Format("Duplicate annotation '{0}' — two definition files share the same name")]
    ErrAnnotationDuplicateName = 0x5005,

    [Level(DiagnosticLevel.Error)]
    [Category(DiagnosticCategory.Semantic)]
    [Format("Unknown annotation '@{0}'")]
    ErrUnknownAnnotation = 0x5006,

    [Level(DiagnosticLevel.Error)]
    [Category(DiagnosticCategory.Semantic)]
    [Format("Annotation '@{0}' cannot be applied to {1}")]
    ErrAnnotationTargetMismatch = 0x5007,

    [Level(DiagnosticLevel.Error)]
    [Category(DiagnosticCategory.Semantic)]
    [Format("Annotation '@{0}' argument '{1}' must be a constant literal")]
    ErrAnnotationArgNotLiteral = 0x5008,

    [Level(DiagnosticLevel.Error)]
    [Category(DiagnosticCategory.Semantic)]
    [Format("Annotation '@{0}' is missing required argument '{1}'")]
    ErrAnnotationArgMissing = 0x5009,

    [Level(DiagnosticLevel.Error)]
    [Category(DiagnosticCategory.Semantic)]
    [Format("Annotation '@{0}' does not accept argument '{1}'")]
    ErrAnnotationArgUnknown = 0x500A,

    [Level(DiagnosticLevel.Error)]
    [Category(DiagnosticCategory.Semantic)]
    [Format("Annotation '@{0}' runtime error: {1}")]
    ErrAnnotationRuntimeError = 0x500B,

    [Level(DiagnosticLevel.Error)]
    [Category(DiagnosticCategory.Semantic)]
    [Format("Annotation '@{0}' returned malformed IR: {1}")]
    ErrAnnotationMalformedResult = 0x500C,

    [Level(DiagnosticLevel.Error)]
    [Category(DiagnosticCategory.Semantic)]
    [Format("Annotations are not allowed inside annotation definition files")]
    ErrAnnotationInAnnotationFile = 0x500D,

    [Level(DiagnosticLevel.Error)]
    [Category(DiagnosticCategory.Semantic)]
    [Format("Annotation '@{0}' argument '{1}' expects {2}, got {3}")]
    ErrAnnotationArgTypeMismatch = 0x500E,

    #endregion

    #region Sides

    [Level(DiagnosticLevel.Error)]
    [Category(DiagnosticCategory.Semantic)]
    [Format("Unknown side '{0}' — expected one of: client, server, shared")]
    ErrUnknownSideName = 0x6001,

    [Level(DiagnosticLevel.Error)]
    [Category(DiagnosticCategory.Semantic)]
    [Format("Symbol '{0}' is {1}-side only and cannot be used in this {2}-side file")]
    ErrSymbolWrongSide = 0x6002,

    #endregion
}