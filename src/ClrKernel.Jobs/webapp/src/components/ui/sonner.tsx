"use client"

import { Toaster as Sonner, type ToasterProps } from "sonner"
import { CircleCheckIcon, InfoIcon, TriangleAlertIcon, OctagonXIcon, Loader2Icon } from "lucide-react"

const Toaster = ({ ...props }: ToasterProps) => {
  return (
    <Sonner
      theme="light"
      className="toaster group"
      icons={{
        success: (
          <CircleCheckIcon className="size-4" />
        ),
        info: (
          <InfoIcon className="size-4" />
        ),
        warning: (
          <TriangleAlertIcon className="size-4" />
        ),
        error: (
          <OctagonXIcon className="size-4" />
        ),
        loading: (
          <Loader2Icon className="size-4 animate-spin" />
        ),
      }}
      style={
        {
          "--normal-bg": "var(--popover)",
          "--normal-text": "var(--popover-foreground)",
          "--normal-border": "var(--border)",
          "--border-radius": "var(--radius)",
          // `richColors` otherwise paints these from sonner's own palette,
          // which is the one place a colour would enter the app from outside
          // the token layer — and its saturated reds and ambers are wrong on
          // cream. Same tints the env chips and failed rows already use.
          "--success-bg": "var(--env-prod-bg)",
          "--success-border": "var(--env-prod-border)",
          "--success-text": "var(--status-success)",
          "--warning-bg": "var(--env-dev-bg)",
          "--warning-border": "var(--env-dev-border)",
          "--warning-text": "var(--status-warning)",
          "--error-bg": "var(--row-failed)",
          "--error-border": "var(--row-failed-border)",
          "--error-text": "var(--destructive)",
          "--info-bg": "var(--primary-soft)",
          "--info-border": "var(--border)",
          "--info-text": "var(--foreground)",
        } as React.CSSProperties
      }
      toastOptions={{
        classNames: {
          toast: "cn-toast",
        },
      }}
      {...props}
    />
  )
}

export { Toaster }
