/**
 * Delivers only the most recent value, once per animation frame.
 *
 * The station publishes per frame received: twelve vehicles at four position reports a second is
 * around fifty updates a second, and every one of them would otherwise replace the map's whole
 * source and re-render twelve panel rows. The display cannot show more than one picture per frame
 * in any case, so the ones in between are work done to be overwritten.
 *
 * **It also makes the two surfaces one picture.** The map and the panel are updated inside a single
 * callback here, from a single snapshot, in a single frame — so there is no moment at which a
 * marker has gone amber and its row has not. That is a rendering property backing up what
 * `appearanceOf` already guarantees about the derivation, and MCS-003 wants both.
 *
 * Nothing is dropped that an operator could have seen, and nothing is *delayed*: the latest value
 * is always the one that arrives. What is dropped is the intermediate states of a burst, which is
 * exactly what a slow connection's backlog draining looks like.
 */
export interface Coalescer<T> {
  /** Takes a value, replacing any that has not been delivered yet. */
  deliver(value: T): void

  /** Cancels a pending frame. Call it when the thing being fed goes away. */
  cancel(): void
}

export function coalesceToFrames<T>(apply: (value: T) => void): Coalescer<T> {
  let pending: { value: T } | null = null
  let frame: number | null = null

  return {
    deliver(value: T) {
      pending = { value }

      //  Already scheduled: the value above has replaced whatever the frame was going to deliver,
      //  which is the whole point. A second request would run the callback twice with the same
      //  value.
      if (frame !== null) return

      frame = requestAnimationFrame(() => {
        frame = null

        //  Boxed rather than tested against a sentinel, because T is allowed to be null or
        //  undefined and a falsy check would silently skip those.
        if (pending === null) return

        const { value: latest } = pending
        pending = null

        apply(latest)
      })
    },

    cancel() {
      if (frame !== null) cancelAnimationFrame(frame)

      frame = null
      pending = null
    },
  }
}
