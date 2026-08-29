import { Check, SwatchBook } from 'lucide-react';
import { Button } from '@/components/ui/button';
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuLabel,
  DropdownMenuTrigger,
} from '@/components/ui/dropdown-menu';
import { Tooltip, TooltipContent, TooltipTrigger } from '@/components/ui/tooltip';
import { ACCENTS, type AccentName } from '../theme/palette';

/**
 * Five accents on one fixed neutral base. Each swatch carries its name as text,
 * not only as colour — a row of coloured dots is unusable to anyone who cannot
 * tell them apart.
 */
export function AccentPicker({
  accent,
  onAccent,
}: {
  accent: AccentName;
  onAccent: (accent: AccentName) => void;
}) {
  return (
    <DropdownMenu>
      <Tooltip>
        <TooltipTrigger asChild>
          <DropdownMenuTrigger asChild>
            <Button variant="ghost" size="icon" className="size-8" aria-label="Change accent colour">
              <SwatchBook className="size-4" aria-hidden="true" />
            </Button>
          </DropdownMenuTrigger>
        </TooltipTrigger>
        <TooltipContent>Accent colour</TooltipContent>
      </Tooltip>
      <DropdownMenuContent align="end" className="w-44">
        <DropdownMenuLabel>Accent</DropdownMenuLabel>
        {ACCENTS.map((option) => (
          <DropdownMenuItem
            key={option.name}
            onSelect={() => onAccent(option.name)}
            className="gap-2"
          >
            <span
              aria-hidden="true"
              className="size-3.5 shrink-0 rounded-full border border-border"
              style={{ background: option.primary }}
            />
            <span className="flex-1">{option.label}</span>
            {option.name === accent && <Check className="size-4" aria-hidden="true" />}
          </DropdownMenuItem>
        ))}
      </DropdownMenuContent>
    </DropdownMenu>
  );
}
