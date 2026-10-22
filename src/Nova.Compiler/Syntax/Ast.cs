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
    TextSpan Span) : Item(Visibility, Name, Span);

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
    TextSpan Span) : Item(Visibility, Name, Span);

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
    TextSpan Span) : Stmt(Span);

/// <summary><see cref="Else"/> is null, a <see cref="BlockStmt"/>, or an <see cref="IfStmt"/>.</summary>
public sealed record IfStmt(Expr Condition, BlockStmt Then, Stmt? Else, TextSpan Span) : Stmt(Span);

public sealed record WhileStmt(Expr Condition, BlockStmt Body, TextSpan Span) : Stmt(Span);

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

/// <summary><c>mut e</c>: only as a call argument for a <c>borrow mut</c> parameter.</summary>
public sealed record MutArgExpr(Expr Operand, TextSpan Span) : Expr(Span);

/// <summary><c>S { f: e, ... }</c>; initialisers in source order.</summary>
public sealed record StructLiteralExpr(Identifier TypeName, ImmutableArray<FieldInit> Fields, TextSpan Span) : Expr(Span);

public sealed record FieldInit(Identifier Name, Expr Value, TextSpan Span) : SyntaxNode(Span);
