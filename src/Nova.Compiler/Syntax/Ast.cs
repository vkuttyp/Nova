using System.Collections.Immutable;

namespace Nova.Compiler.Syntax;

// Abstract syntax tree for the M1 grammar (docs/grammar.md §2). Nodes are immutable and carry
// the source span they cover. Error nodes (ErrorExpr, MissingType) stand in for unparsable
// input so later passes can run without null checks.

public abstract record SyntaxNode(TextSpan Span);

public sealed record Identifier(Symbol Name, TextSpan Span) : SyntaxNode(Span)
{
    /// <summary>Placeholder for an identifier the parser expected but did not find.</summary>
    public bool IsMissing { get; init; }
}

public sealed record QualifiedName(ImmutableArray<Identifier> Parts, TextSpan Span) : SyntaxNode(Span);

public sealed record CompilationUnit(
    SourceText Source,
    NamespaceDecl? Namespace,
    ImmutableArray<Item> Items,
    TextSpan Span) : SyntaxNode(Span);

public sealed record NamespaceDecl(QualifiedName Name, TextSpan Span) : SyntaxNode(Span);

public enum Visibility
{
    Default,
    Public,
    Internal,
    Private,
}

public abstract record Item(Visibility Visibility, Identifier Name, TextSpan Span) : SyntaxNode(Span);

/// <summary>
/// A function. Exactly one of <see cref="Body"/> and <see cref="ExpressionBody"/> is set.
/// <see cref="Receiver"/> is set for a method's <c>this</c> parameter (type-system.md §12).
/// </summary>
public sealed record FunctionDecl(
    Visibility Visibility,
    Identifier Name,
    Receiver? Receiver,
    ImmutableArray<Parameter> Parameters,
    TypeSyntax? ReturnType,
    BlockStmt? Body,
    Expr? ExpressionBody,
    TextSpan Span) : Item(Visibility, Name, Span)
{
    /// <summary><c>fn f&lt;T, U&gt;</c> (type-system.md §20); empty for a non-generic function.</summary>
    public ImmutableArray<TypeParameter> TypeParameters { get; init; } = [];

    /// <summary>The <c>region</c> parameters, declared before the type parameters (type-system.md §25.1).</summary>
    public ImmutableArray<TypeParameter> RegionParameters { get; init; } = [];
}

/// <summary>A type parameter <c>T</c>, with the interface bounds of <c>T: I + J</c> (§21; empty without).</summary>
public sealed record TypeParameter(Identifier Name, ImmutableArray<TypeSyntax> Bounds, TextSpan Span) : SyntaxNode(Span)
{
    /// <summary><c>region r</c>: a region parameter (type-system.md §25.1), not a type.</summary>
    public bool IsRegion { get; init; }
}

public enum ReceiverKind
{
    Value,
    Borrow,
    BorrowMut,
}

/// <summary>The <c>this</c> receiver of a method: <c>this</c>, <c>borrow this</c> or <c>borrow mut this</c>.</summary>
public sealed record Receiver(ReceiverKind Kind, TextSpan Span) : SyntaxNode(Span);

public sealed record StructDecl(
    Visibility Visibility,
    Identifier Name,
    ImmutableArray<FieldDecl> Fields,
    ImmutableArray<FunctionDecl> Functions,
    TextSpan Span) : Item(Visibility, Name, Span)
{
    /// <summary>The struct's <c>drop { ... }</c> block, if any (type-system.md §14.2).</summary>
    public DropBlock? Drop { get; init; }

    /// <summary><c>struct S&lt;T&gt;</c> (type-system.md §20); empty for a non-generic struct.</summary>
    public ImmutableArray<TypeParameter> TypeParameters { get; init; } = [];

    /// <summary>The <c>region</c> parameters, declared before the type parameters (type-system.md §25.1).</summary>
    public ImmutableArray<TypeParameter> RegionParameters { get; init; } = [];
}

public sealed record DropBlock(BlockStmt Body, TextSpan Span) : SyntaxNode(Span);

public sealed record EnumDecl(
    Visibility Visibility,
    Identifier Name,
    ImmutableArray<VariantDecl> Variants,
    TextSpan Span) : Item(Visibility, Name, Span)
{
    /// <summary><c>enum E&lt;T&gt;</c> (type-system.md §20); empty for a non-generic enum.</summary>
    public ImmutableArray<TypeParameter> TypeParameters { get; init; } = [];

    /// <summary>The <c>region</c> parameters, declared before the type parameters (type-system.md §25.1).</summary>
    public ImmutableArray<TypeParameter> RegionParameters { get; init; } = [];
}

/// <summary><c>interface I&lt;T&gt; { fn m(borrow this) -> R; … }</c> (type-system.md §21.1): signatures only.</summary>
public sealed record InterfaceDecl(
    Visibility Visibility,
    Identifier Name,
    ImmutableArray<FunctionDecl> Methods,
    TextSpan Span) : Item(Visibility, Name, Span)
{
    public ImmutableArray<TypeParameter> TypeParameters { get; init; } = [];
}

/// <summary>
/// <c>impl I for T { … }</c> (type-system.md §21.1). <see cref="Item.Name"/> is the interface's name
/// (impl blocks are not named items).
/// </summary>
public sealed record ImplDecl(
    Visibility Visibility,
    Identifier Name,
    TypeSyntax Interface,
    TypeSyntax ForType,
    ImmutableArray<FunctionDecl> Functions,
    TextSpan Span) : Item(Visibility, Name, Span);

public sealed record VariantDecl(Identifier Name, ImmutableArray<TypeSyntax> Payload, TextSpan Span) : SyntaxNode(Span);

public sealed record FieldDecl(Visibility Visibility, Identifier Name, TypeSyntax Type, TextSpan Span) : SyntaxNode(Span);

public sealed record ConstDecl(
    Visibility Visibility,
    Identifier Name,
    TypeSyntax Type,
    Expr Value,
    TextSpan Span) : Item(Visibility, Name, Span);

public sealed record Parameter(Identifier Name, TypeSyntax Type, TextSpan Span) : SyntaxNode(Span);

// ---- Types ----

public abstract record TypeSyntax(TextSpan Span) : SyntaxNode(Span);

public sealed record NamedType(QualifiedName Name, TextSpan Span) : TypeSyntax(Span);

public sealed record MissingType(TextSpan Span) : TypeSyntax(Span);

/// <summary><c>T?</c> (type-system.md §22.1).</summary>
public sealed record OptionalType(TypeSyntax Inner, TextSpan Span) : TypeSyntax(Span);

/// <summary><c>[T; N]</c> (type-system.md §16.1); <see cref="Length"/> is an integer literal.</summary>
public sealed record ArrayType(TypeSyntax Element, Expr Length, TextSpan Span) : TypeSyntax(Span);

/// <summary>A name with type arguments: <c>Slice&lt;T&gt;</c>, <c>SliceMut&lt;T&gt;</c> (type-system.md §16.1).</summary>
public sealed record GenericType(QualifiedName Name, ImmutableArray<TypeSyntax> Arguments, TextSpan Span) : TypeSyntax(Span);

/// <summary>
/// <c>borrow T</c>, <c>borrow mut T</c>; <see cref="Sources"/> is the <c>(a, b)</c> list allowed in
/// return types (lifetimes.md §7), default when absent.
/// </summary>
public sealed record ReferenceType(bool IsMutable, ImmutableArray<Identifier> Sources, TypeSyntax Pointee, TextSpan Span) : TypeSyntax(Span)
{
    public bool HasSources { get; init; }
}

// ---- Statements ----

public abstract record Stmt(TextSpan Span) : SyntaxNode(Span);

public sealed record BlockStmt(ImmutableArray<Stmt> Statements, TextSpan Span) : Stmt(Span);

public sealed record LocalDeclStmt(
    bool IsMutable,
    Identifier Name,
    TypeSyntax? Type,
    Expr? Initializer,
    TextSpan Span) : Stmt(Span)
{
    /// <summary>Declared with <c>using</c> (type-system.md §14.6).</summary>
    public bool IsUsing { get; init; }
}

/// <summary><c>defer s;</c> (type-system.md §14.7).</summary>
public sealed record DeferStmt(Stmt Body, TextSpan Span) : Stmt(Span);

/// <summary><c>match (e) { pattern => { ... } ... }</c> (type-system.md §15.2).</summary>
public sealed record MatchStmt(Expr Scrutinee, ImmutableArray<MatchArm> Arms, TextSpan Span) : Stmt(Span);

public sealed record MatchArm(Pattern Pattern, BlockStmt Body, TextSpan Span) : SyntaxNode(Span);

public abstract record Pattern(TextSpan Span) : SyntaxNode(Span);

public sealed record WildcardPattern(TextSpan Span) : Pattern(Span);

/// <summary>The <c>null</c> pattern of an optional (§22.1).</summary>
public sealed record NullPattern(TextSpan Span) : Pattern(Span);

/// <summary>An integer (possibly negated) or bool literal.</summary>
public sealed record LiteralPattern(Expr Literal, TextSpan Span) : Pattern(Span);

/// <summary><c>E.V</c> or <c>E.V(x, _)</c>; a null binding is <c>_</c>. <see cref="Bindings"/> is default without parentheses.</summary>
public sealed record VariantPattern(QualifiedName Path, ImmutableArray<Identifier?> Bindings, TextSpan Span) : Pattern(Span)
{
    /// <summary>Parallel to <see cref="Bindings"/>: true for <c>var x</c> (type-system.md §18.4). Default when there are no bindings.</summary>
    public ImmutableArray<bool> Mutable { get; init; }

    public bool IsMutable(int index) => !Mutable.IsDefault && Mutable[index];
}

/// <summary><see cref="Else"/> is null, a <see cref="BlockStmt"/>, or an <see cref="IfStmt"/>.</summary>
public sealed record IfStmt(Expr Condition, BlockStmt Then, Stmt? Else, TextSpan Span) : Stmt(Span);

public sealed record WhileStmt(Expr Condition, BlockStmt Body, TextSpan Span) : Stmt(Span);

/// <summary><c>for (x in e) { }</c> or <c>for (i in lo..hi) { }</c> (type-system.md §16.4).</summary>
public sealed record ForStmt(Identifier Variable, Expr Start, Expr? End, BlockStmt Body, TextSpan Span) : Stmt(Span);

public sealed record LoopStmt(BlockStmt Body, TextSpan Span) : Stmt(Span);

public sealed record BreakStmt(TextSpan Span) : Stmt(Span);

public sealed record ContinueStmt(TextSpan Span) : Stmt(Span);

public sealed record ReturnStmt(Expr? Value, TextSpan Span) : Stmt(Span);

public enum AssignOp
{
    Assign,
    Add,
    Subtract,
    Multiply,
    Divide,
    Remainder,
    BitAnd,
    BitOr,
    BitXor,
    ShiftLeft,
    ShiftRight,
}

public sealed record AssignStmt(Expr Target, AssignOp Op, Expr Value, TextSpan Span) : Stmt(Span);

public sealed record ExprStmt(Expr Expression, TextSpan Span) : Stmt(Span);

// ---- Expressions ----

public abstract record Expr(TextSpan Span) : SyntaxNode(Span);

public sealed record IntLiteralExpr(ulong Value, IntSuffix Suffix, TextSpan Span) : Expr(Span);

public sealed record FloatLiteralExpr(string Digits, FloatSuffix Suffix, TextSpan Span) : Expr(Span);

public sealed record StringLiteralExpr(string Value, TextSpan Span) : Expr(Span);

public sealed record CharLiteralExpr(int Value, TextSpan Span) : Expr(Span);

public sealed record BoolLiteralExpr(bool Value, TextSpan Span) : Expr(Span);

/// <summary><c>null</c>: an absent optional (type-system.md §22.1).</summary>
public sealed record NullLiteralExpr(TextSpan Span) : Expr(Span);

/// <summary>A name, optionally with generic arguments (<c>truncate&lt;i64&gt;</c>, grammar.md §2.1).</summary>
public sealed record NameExpr(Identifier Name, ImmutableArray<TypeSyntax> TypeArguments, TextSpan Span) : Expr(Span);

public sealed record ParenExpr(Expr Inner, TextSpan Span) : Expr(Span);

public enum UnaryOp
{
    Negate,
    Not,
}

public sealed record UnaryExpr(UnaryOp Op, Expr Operand, TextSpan Span) : Expr(Span);

public enum BinaryOp
{
    LogicalOr,
    LogicalAnd,
    Equal,
    NotEqual,
    Less,
    LessEqual,
    Greater,
    GreaterEqual,
    BitOr,
    BitXor,
    BitAnd,
    ShiftLeft,
    ShiftRight,
    Add,
    Subtract,
    Multiply,
    Divide,
    Remainder,
}

public sealed record BinaryExpr(Expr Left, BinaryOp Op, Expr Right, TextSpan Span) : Expr(Span);

public sealed record CastExpr(Expr Operand, TypeSyntax Type, TextSpan Span) : Expr(Span);

public sealed record CallExpr(Expr Callee, ImmutableArray<Expr> Arguments, TextSpan Span) : Expr(Span);

public sealed record MemberExpr(Expr Target, Identifier Member, TextSpan Span) : Expr(Span);

public sealed record ErrorExpr(TextSpan Span) : Expr(Span);

public sealed record ThisExpr(TextSpan Span) : Expr(Span);

/// <summary><c>borrow e</c> or <c>borrow mut e</c>.</summary>
public sealed record BorrowExpr(bool IsMutable, Expr Operand, TextSpan Span) : Expr(Span);

/// <summary><c>move p</c>: moves out of a place (type-system.md §14.3).</summary>
public sealed record MoveExpr(Expr Operand, TextSpan Span) : Expr(Span);

/// <summary><c>mut e</c>: only as a call argument for a <c>borrow mut</c> parameter.</summary>
public sealed record MutArgExpr(Expr Operand, TextSpan Span) : Expr(Span);

/// <summary><c>S { f: e, ... }</c>; initialisers in source order.</summary>
public sealed record StructLiteralExpr(Identifier TypeName, ImmutableArray<FieldInit> Fields, TextSpan Span) : Expr(Span)
{
    /// <summary><c>Pair&lt;u8&gt; { … }</c>: explicit type arguments (§20.2); empty when inferred.</summary>
    public ImmutableArray<TypeSyntax> TypeArguments { get; init; } = [];
}

/// <summary><c>[a, b, c]</c> (type-system.md §16.2).</summary>
public sealed record ArrayLiteralExpr(ImmutableArray<Expr> Elements, TextSpan Span) : Expr(Span);

/// <summary><c>[v; N]</c>; <see cref="Count"/> is an integer literal.</summary>
public sealed record ArrayRepeatExpr(Expr Value, Expr Count, TextSpan Span) : Expr(Span);

/// <summary><c>a[i]</c>.</summary>
public sealed record IndexExpr(Expr Target, Expr Index, TextSpan Span) : Expr(Span);

/// <summary><c>a[lo..hi]</c>; either bound may be absent (type-system.md §16.3).</summary>
public sealed record SliceExpr(Expr Target, Expr? Low, Expr? High, TextSpan Span) : Expr(Span);

/// <summary><c>e?</c>: propagate an <c>Err</c> (type-system.md §17.4).</summary>
public sealed record TryExpr(Expr Operand, TextSpan Span) : Expr(Span);

/// <summary><c>e!</c>: unwrap an <c>Ok</c> or panic.</summary>
public sealed record UnwrapExpr(Expr Operand, TextSpan Span) : Expr(Span);

public sealed record FieldInit(Identifier Name, Expr Value, TextSpan Span) : SyntaxNode(Span);
