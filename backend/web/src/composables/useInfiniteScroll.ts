import { onBeforeUnmount, onMounted, type Ref } from 'vue'

/**
 * Calls load whenever the sentinel at the end of a list comes near the visible part of the scrolling main area.
 * Observing anew after each load fires again while the sentinel is still in view, e.g. for short pages.
 */
export function useInfiniteScroll(sentinel: Ref<HTMLElement | null>, load: () => Promise<unknown>) {
  let observer: IntersectionObserver | undefined
  onMounted(() => {
    observer = new IntersectionObserver(async (entries) => {
      if (!entries.some((e) => e.isIntersecting) || !sentinel.value) return
      await load()
      observer?.unobserve(sentinel.value)
      observer?.observe(sentinel.value)
    }, { root: document.querySelector('[data-scroll-root]'), rootMargin: '600px' })
    if (sentinel.value) observer.observe(sentinel.value)
  })
  onBeforeUnmount(() => observer?.disconnect())
}
