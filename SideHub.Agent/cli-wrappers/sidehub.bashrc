# SideHub bash init for interactive PTY sessions.
#
# Why this file exists: the agent prepends its cli-wrappers directory to PATH
# before spawning bash, but a user's ~/.bashrc commonly prepends ~/.local/bin
# (or similar) to PATH after that, shadowing the wrapper. We re-prepend
# cli-wrappers after the user's init has run so the wrapper always wins.

# Mirror the user's normal interactive bash init. We're invoked via
# `bash --rcfile <this> -i` (no -l), so we re-source the standard files to
# preserve user PATH, aliases, prompt, etc.
[ -f /etc/profile ] && . /etc/profile
if [ -f ~/.bash_profile ]; then
  . ~/.bash_profile
elif [ -f ~/.profile ]; then
  . ~/.profile
fi
# Most distros' .profile sources .bashrc, but not all — source it explicitly
# if nothing did. Guard with a marker so we don't double-source.
if [ -z "$_SIDEHUB_BASHRC_SOURCED" ] && [ -f ~/.bashrc ]; then
  . ~/.bashrc
fi
export _SIDEHUB_BASHRC_SOURCED=1

# Re-prepend SideHub cli-wrappers so user PATH manipulations can't shadow it.
if [ -n "$SIDEHUB_CLI_WRAPPERS" ] && [ -d "$SIDEHUB_CLI_WRAPPERS" ]; then
  case ":$PATH:" in
    :"$SIDEHUB_CLI_WRAPPERS":*) ;;
    *) export PATH="$SIDEHUB_CLI_WRAPPERS:$PATH" ;;
  esac
fi
