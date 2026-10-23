namespace PoniExp.Client.Completion;

/// <summary>The shell side of tab completion: each script asks <c>poniexp-client complete</c> for the candidates.</summary>
public static class CompletionScripts
{
    public const string Bash = """
        # poniexp-client tab completion (bash). In ~/.bashrc: source <(poniexp-client completion bash)
        _poniexp_client() {
            local IFS=$'\n'
            COMPREPLY=($(poniexp-client complete --bash --line "${COMP_LINE:0:COMP_POINT}" 2>/dev/null))
        }
        complete -o default -F _poniexp_client poniexp-client poniexp-client.exe

        """;

    public const string Zsh = """
        # poniexp-client tab completion (zsh). In ~/.zshrc: source <(poniexp-client completion zsh)
        (( $+functions[compdef] )) || { autoload -Uz compinit && compinit }
        _poniexp_client() {
            local -a candidates
            candidates=("${(@f)$(poniexp-client complete -- "${(@)words[2,CURRENT]}" 2>/dev/null)}")
            if [[ -n ${candidates[1]} ]]; then
                compadd -a candidates
            else
                _files
            fi
        }
        compdef _poniexp_client poniexp-client

        """;

    // The AST's text stops at the last word, so a cursor after a trailing space is past its end: pad to reach it.
    public const string PowerShell = """
        # poniexp-client tab completion (PowerShell). In $PROFILE: poniexp-client completion powershell | Out-String | Invoke-Expression
        Register-ArgumentCompleter -Native -CommandName 'poniexp-client', 'poniexp-client.exe' -ScriptBlock {
            param($wordToComplete, $commandAst, $cursorPosition)
            $line = $commandAst.Extent.Text
            $upTo = $cursorPosition - $commandAst.Extent.StartOffset
            if ($upTo -lt $line.Length) { $line = $line.Substring(0, $upTo) } else { $line = $line + (' ' * ($upTo - $line.Length)) }
            poniexp-client complete --line $line 2>$null | ForEach-Object {
                [System.Management.Automation.CompletionResult]::new($_, $_, 'ParameterValue', $_)
            }
        }

        """;

    /// <summary>The script for <paramref name="shell"/> (<c>bash</c>, <c>zsh</c> or <c>powershell</c>).</summary>
    public static string For(string shell) => shell switch
    {
        "bash" => Bash,
        "zsh" => Zsh,
        "powershell" or "pwsh" => PowerShell,
        _ => throw new ArgumentException($"no completion for '{shell}' (bash, zsh or powershell)"),
    };
}
