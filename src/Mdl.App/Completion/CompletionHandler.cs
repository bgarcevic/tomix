using Mdl.Core.Results;

namespace Mdl.App.Completion;

/// <summary>
/// Generates a static shell completion script for the top-level <c>mdl</c> commands.
/// The command list is supplied by the caller so the script always matches the live command tree.
/// Unsupported shells are rejected with exit code 2 (invalid arguments).
/// </summary>
public sealed class CompletionHandler
{
    private static readonly IReadOnlyList<string> SupportedShells =
        ["bash", "zsh", "fish", "powershell"];

    public MdlResult<CompletionResult> Generate(string shell, IReadOnlyList<string> commands)
    {
        var normalized = shell.Trim().ToLowerInvariant();

        if (!SupportedShells.Contains(normalized))
            return MdlResult<CompletionResult>.Fail(
                code: "MDL_COMPLETION_UNSUPPORTED_SHELL",
                message: $"Unsupported shell: {shell}. Supported shells: {string.Join(", ", SupportedShells)}.",
                exitCode: 2);

        var script = normalized switch
        {
            "bash"       => Bash(commands),
            "zsh"        => Zsh(commands),
            "fish"       => Fish(commands),
            "powershell" => PowerShell(commands),
            _            => ""
        };

        return MdlResult<CompletionResult>.Ok(new CompletionResult(normalized, script));
    }

    private static string Bash(IReadOnlyList<string> commands) =>
        $$"""
        # mdl bash completion
        # Install: source <(mdl completion bash)   (or write to /etc/bash_completion.d/mdl)
        _mdl_completion()
        {
            local cur="${COMP_WORDS[COMP_CWORD]}"
            local commands="{{Join(commands)}}"
            if [ "$COMP_CWORD" -eq 1 ]; then
                COMPREPLY=( $(compgen -W "$commands" -- "$cur") )
            fi
        }
        complete -F _mdl_completion mdl
        """;

    private static string Zsh(IReadOnlyList<string> commands) =>
        $$"""
        #compdef mdl
        # mdl zsh completion
        # Install: mdl completion zsh > "${fpath[1]}/_mdl"
        _mdl()
        {
            local -a commands
            commands=({{Join(commands)}})
            _arguments '1: :->command' '*::arg:->args'
            case $state in
                command) _describe 'command' commands ;;
            esac
        }
        compdef _mdl mdl
        """;

    private static string Fish(IReadOnlyList<string> commands) =>
        $$"""
        # mdl fish completion
        # Install: mdl completion fish > ~/.config/fish/completions/mdl.fish
        complete -c mdl -f
        complete -c mdl -n '__fish_use_subcommand' -a '{{Join(commands)}}'
        """;

    private static string PowerShell(IReadOnlyList<string> commands) =>
        $$"""
        # mdl PowerShell completion
        # Install: mdl completion powershell | Out-String | Invoke-Expression  (add to $PROFILE)
        Register-ArgumentCompleter -Native -CommandName mdl -ScriptBlock {
            param($wordToComplete, $commandAst, $cursorPosition)
            @({{string.Join(", ", commands.Select(c => $"'{c}'"))}}) |
                Where-Object { $_ -like "$wordToComplete*" } |
                ForEach-Object {
                    [System.Management.Automation.CompletionResult]::new($_, $_, 'ParameterValue', $_)
                }
        }
        """;

    private static string Join(IReadOnlyList<string> commands) => string.Join(" ", commands);
}
