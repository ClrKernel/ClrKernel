import { Check, Monitor, Moon, Sun } from 'lucide-react';
import { Button } from '@/components/ui/button';
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuLabel,
  DropdownMenuTrigger,
} from '@/components/ui/dropdown-menu';
import { Tooltip, TooltipContent, TooltipTrigger } from '@/components/ui/tooltip';
import type { ThemeName } from '../theme/palette';
import type { ThemeMode } from '../theme/theme';

/**
 * Light, dark, or the OS's answer.
 *
 * Three entries and not a toggle: "follow the system" is a real choice, not the
 * absence of one, and a toggle cannot express it. The button shows the theme you
 * are actually in rather than the mode you picked — on `system` that is the more
 * useful of the two, because it is the one that changes under you.
 */
export function ThemePicker({
  mode,
  theme,
  onMode,
}: {
  mode: ThemeMode;
  /** What `mode` resolves to right now — the icon on the button. */
  theme: ThemeName;
  onMode: (mode: ThemeMode) => void;
}) {
  const options: { value: ThemeMode; label: string; Icon: typeof Sun }[] = [
    { value: 'light', label: 'Light', Icon: Sun },
    { value: 'dark', label: 'Dark', Icon: Moon },
    { value: 'system', label: 'System', Icon: Monitor },
  ];
  const Showing = theme === 'dark' ? Moon : Sun;

  return (
    <DropdownMenu>
      <Tooltip>
        <TooltipTrigger asChild>
          <DropdownMenuTrigger asChild>
            <Button variant="ghost" size="icon" className="size-8" aria-label="Change theme">
              <Showing className="size-4" aria-hidden="true" />
            </Button>
          </DropdownMenuTrigger>
        </TooltipTrigger>
        <TooltipContent>
          {mode === 'system' ? `Theme — system (${theme})` : 'Theme'}
        </TooltipContent>
      </Tooltip>
      <DropdownMenuContent align="end" className="w-44">
        <DropdownMenuLabel>Theme</DropdownMenuLabel>
        {options.map((option) => (
          <DropdownMenuItem
            key={option.value}
            onSelect={() => onMode(option.value)}
            className="gap-2"
          >
            <option.Icon className="size-3.5 shrink-0" aria-hidden="true" />
            <span className="flex-1">{option.label}</span>
            {option.value === mode && <Check className="size-4" aria-hidden="true" />}
          </DropdownMenuItem>
        ))}
      </DropdownMenuContent>
    </DropdownMenu>
  );
}
