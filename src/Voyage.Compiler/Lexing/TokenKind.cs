namespace Voyage.Compiler.Lexing;

/// <summary>
/// Every token kind the lexer can produce. Organized to mirror
/// docs/spec/grammar.md's "Appendix: Supported Tokens" exactly, so any
/// discrepancy between the spec and this enum is easy to spot by
/// section-by-section comparison.
/// </summary>
public enum TokenKind
{
    // --- End of file / error --------------------------------------------
    EndOfFile,
    Invalid,

    // --- Identifiers and literals ------------------------------------
    Identifier,
    IntegerLiteral,
    FloatLiteral,
    StringLiteral,          // a string literal with no interpolation, or
                             // a single segment of an interpolated one
    BooleanLiteral,         // true / false
    NilLiteral,

    // --- String interpolation structural tokens -------------------------
    // "text \( expr ) more text" lexes as:
    //   InterpolationStringStart "text "
    //   ... tokens for `expr` ...
    //   InterpolationStringMiddle " more text"   (if another \( follows)
    //   InterpolationStringEnd    ""              (closing quote reached)
    // A plain, non-interpolated string is a single StringLiteral token.
    InterpolationStringStart,
    InterpolationStringMiddle,
    InterpolationStringEnd,

    // --- Declarations (grammar.md Appendix: Declarations) ----------------
    KwFunc,
    KwStruct,
    KwEnum,
    KwProtocol,
    KwExtension,
    KwActor,
    KwCase,
    KwAssociatedtype,

    // --- Bindings ----------------------------------------------------
    KwLet,
    KwVar,

    // --- Types ---------------------------------------------------------
    KwSelfType,     // `Self` (capital S — the type, distinct from `self`)
    KwAny,          // existential marker: `any P`
    KwSome,         // opaque marker: `some P`
    KwOptional,     // `Optional` written out, rarely used vs. `?` sugar

    // --- Control flow ----------------------------------------------------
    KwIf,
    KwElse,
    KwGuard,
    KwSwitch,
    KwDefault,
    KwFor,
    KwIn,
    KwWhile,
    KwRepeat,
    KwBreak,
    KwContinue,
    KwReturn,
    KwFallthrough,

    // --- Error handling --------------------------------------------------
    KwThrow,
    KwThrows,
    KwTry,
    KwCatch,
    KwDo,

    // --- Concurrency -----------------------------------------------------
    KwAsync,
    KwAwait,
    KwTask,
    KwSpawn,
    KwJoin,
    KwAtomic,

    // --- Access control --------------------------------------------------
    KwPublic,
    KwInternal,
    KwPrivate,
    KwFileprivate,

    // --- Modifiers ---------------------------------------------------
    KwStatic,
    KwMutating,
    KwFinal,
    KwOverride,
    KwWeak,

    // --- Memory / cleanup --------------------------------------------
    KwDefer,
    KwUsing,

    // --- Literals (keyword-shaped) ----------------------------------
    KwTrue,
    KwFalse,
    KwNil,

    // --- Miscellaneous -----------------------------------------------
    KwImport,
    KwWhere,
    KwAs,
    KwIs,
    KwSelfValue,    // `self` (lowercase — the instance, distinct from `Self`)
    KwSuper,

    // --- Attributes (grammar.md Appendix: Attributes) ---------------------
    AtMain,         // @main
    AtSyncSafe,     // @syncSafe

    // --- Operators: arithmetic -------------------------------------------
    Plus,           // +
    Minus,          // -
    Star,           // *
    Slash,          // /
    Percent,        // %

    // --- Operators: comparison --------------------------------------
    EqualEqual,     // ==
    BangEqual,      // !=
    Less,           // <
    LessEqual,      // <=
    Greater,        // >
    GreaterEqual,   // >=

    // --- Operators: logical --------------------------------------------
    AmpAmp,         // &&
    PipePipe,       // ||
    Bang,           // !  (also doubles as force-unwrap postfix)

    // --- Operators: assignment -------------------------------------------
    Equal,          // =
    PlusEqual,      // +=
    MinusEqual,     // -=
    StarEqual,      // *=
    SlashEqual,     // /=

    // --- Operators: range --------------------------------------------
    RangeHalfOpen,  // ..<
    RangeClosed,    // ...

    // --- Operators: optional handling -------------------------------
    Question,       // ?   (Optional<T> sugar, ternary, chaining)
    QuestionQuestion, // ?? (nil-coalescing)
    // Bang above doubles as force-unwrap (`!`) postfix.

    // --- Operators: type/casting --------------------------------------
    Colon,          // :   (also used structurally, see Punctuation)
    KwAsQuestion,   // as?
    KwAsBang,       // as!

    // --- Operators: function/closure ------------------------------------
    Arrow,          // ->
    // `in` is KwIn above, shared between for-in and closures.

    // --- Operators: member/scope -------------------------------------
    Dot,            // .
    ColonColon,     // :: (module-qualified access, provisional per grammar.md)

    // --- Protocol composition / generics -----------------------------
    Amp,            // &  (protocol composition: `any Drawable & Equatable`)

    // --- Punctuation & grouping ----------------------------------------
    LParen,         // (
    RParen,         // )
    LBrace,         // {
    RBrace,         // }
    LBracket,       // [
    RBracket,       // ]
    Comma,          // ,
    Semicolon,      // ;  (optional statement separator on a shared line)

    // --- Attribute-generic marker ---------------------------------------
    // Any `@Identifier` not recognized as a specific known attribute
    // (AtMain / AtSyncSafe) lexes as this, carrying the name in the
    // token's Text — future attributes don't require lexer changes.
    AtUnknown,

    // --- Structural ----------------------------------------------------
    // voyage-lang is newline-sensitive (no required semicolons); the
    // lexer surfaces newlines as tokens and leaves it to Parsing/ to
    // decide when a newline terminates a statement vs. is insignificant
    // (e.g. inside an open paren/bracket), matching how Swift's own
    // lexer defers this judgment to the parser rather than deciding it
    // lexically.
    Newline,
}
